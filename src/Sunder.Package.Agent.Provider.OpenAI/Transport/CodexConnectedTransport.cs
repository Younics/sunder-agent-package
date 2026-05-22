using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatToolMode = Microsoft.Extensions.AI.ChatToolMode;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

public sealed class CodexConnectedTransport(CodexConnectedAuthStrategy codexConnectedAuthStrategy, HttpClient httpClient)
{
    private readonly CodexConnectedAuthStrategy _codexConnectedAuthStrategy = codexConnectedAuthStrategy;
    private readonly HttpClient _httpClient = httpClient;
    private static readonly string UserAgent =
        $"Sunder/{typeof(CodexConnectedTransport).Assembly.GetName().Version} ({Environment.OSVersion.Platform}; {Environment.OSVersion.VersionString}; {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

    public string TransportId { get; } = "codex-connected";

    public async IAsyncEnumerable<ChatResponseUpdate> StreamResponseAsync(
        OpenAiCodexSession session,
        AgentChatClientContext context,
        IReadOnlyList<AIChatMessage> messages,
        ChatOptions? options,
        CodexResponseContinuationStore continuationStore,
        string responseId,
        string messageId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolAware = options?.ToolMode != AIChatToolMode.None && options?.Tools is { Count: > 0 };
        using var initialAttempt = await SendAsync(session, context, messages, options, toolAware, continuationStore, disableContinuation: false, cancellationToken);
        var initialResponse = initialAttempt.Response;
        if (initialResponse.IsSuccessStatusCode)
        {
            await foreach (var update in ParseAndRecordContinuationAsync(initialResponse, context, options, initialAttempt.Request, continuationStore, responseId, messageId, toolAware, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        var initialResponseContent = await initialResponse.Content.ReadAsStringAsync(cancellationToken);
        if (IsContinuationFailure(initialAttempt.Request, initialResponse, initialResponseContent))
        {
            continuationStore.Clear(options?.ConversationId);
            await LogAsync(context, AgentLogLevel.Debug, "openai.codex.continuation.fallback", "Codex response continuation was rejected; retrying with full prompt.", cancellationToken: cancellationToken);
            using var fallbackAttempt = await SendAsync(session, context, messages, options, toolAware, continuationStore, disableContinuation: true, cancellationToken);
            var fallbackResponse = fallbackAttempt.Response;
            if (fallbackResponse.IsSuccessStatusCode)
            {
                await foreach (var update in ParseAndRecordContinuationAsync(fallbackResponse, context, options, fallbackAttempt.Request, continuationStore, responseId, messageId, toolAware, cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            var fallbackResponseContent = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
            if (IsAuthenticationFailure(fallbackResponse.StatusCode, fallbackResponseContent))
            {
                var refreshedSession = await _codexConnectedAuthStrategy.TryRefreshSessionAsync(session, cancellationToken);
                if (refreshedSession is not null)
                {
                    using var refreshedFallbackAttempt = await SendAsync(refreshedSession, context, messages, options, toolAware, continuationStore, disableContinuation: true, cancellationToken);
                    var refreshedFallbackResponse = refreshedFallbackAttempt.Response;
                    if (refreshedFallbackResponse.IsSuccessStatusCode)
                    {
                        await foreach (var update in ParseAndRecordContinuationAsync(refreshedFallbackResponse, context, options, refreshedFallbackAttempt.Request, continuationStore, responseId, messageId, toolAware, cancellationToken))
                        {
                            yield return update;
                        }

                        yield break;
                    }

                    fallbackResponseContent = await refreshedFallbackResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw CreateHttpException(
                        toolAware ? "Codex-connected tool request failed after auth refresh" : "Codex-connected request failed after auth refresh",
                        refreshedFallbackResponse,
                        fallbackResponseContent);
                }

                throw new AgentChatProviderException(
                    "codex-auth-required",
                    "### Codex authorization required\n\nYour saved Codex session could not be refreshed silently. Open **Settings -> Packages -> Sunder Agent Provider OpenAI**, click **Authorize**, and then retry.",
                    "codex-auth-required");
            }

            throw CreateHttpException(
                toolAware ? "Codex-connected tool request failed after continuation fallback" : "Codex-connected request failed after continuation fallback",
                fallbackResponse,
                fallbackResponseContent);
        }

        if (IsAuthenticationFailure(initialResponse.StatusCode, initialResponseContent))
        {
            var refreshStopwatch = Stopwatch.StartNew();
            await LogAsync(context, AgentLogLevel.Information, "openai.codex.auth_refresh.start", "Refreshing Codex auth session.", cancellationToken: cancellationToken);
            var refreshedSession = await _codexConnectedAuthStrategy.TryRefreshSessionAsync(session, cancellationToken);
            await LogAsync(
                context,
                AgentLogLevel.Information,
                "openai.codex.auth_refresh.completed",
                refreshedSession is null ? "not refreshed" : "refreshed",
                refreshStopwatch.ElapsedMilliseconds,
                cancellationToken: cancellationToken);
            if (refreshedSession is not null)
            {
                using var retryAttempt = await SendAsync(refreshedSession, context, messages, options, toolAware, continuationStore, disableContinuation: false, cancellationToken);
                var retryResponse = retryAttempt.Response;
                if (retryResponse.IsSuccessStatusCode)
                {
                    await foreach (var update in ParseAndRecordContinuationAsync(retryResponse, context, options, retryAttempt.Request, continuationStore, responseId, messageId, toolAware, cancellationToken))
                    {
                        yield return update;
                    }

                    yield break;
                }

                var retryResponseContent = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
                if (IsContinuationFailure(retryAttempt.Request, retryResponse, retryResponseContent))
                {
                    continuationStore.Clear(options?.ConversationId);
                    await LogAsync(context, AgentLogLevel.Debug, "openai.codex.continuation.fallback", "Codex response continuation was rejected after auth refresh; retrying with full prompt.", cancellationToken: cancellationToken);
                    using var fallbackAttempt = await SendAsync(refreshedSession, context, messages, options, toolAware, continuationStore, disableContinuation: true, cancellationToken);
                    var fallbackResponse = fallbackAttempt.Response;
                    if (fallbackResponse.IsSuccessStatusCode)
                    {
                        await foreach (var update in ParseAndRecordContinuationAsync(fallbackResponse, context, options, fallbackAttempt.Request, continuationStore, responseId, messageId, toolAware, cancellationToken))
                        {
                            yield return update;
                        }

                        yield break;
                    }

                    retryResponseContent = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw CreateHttpException(
                        toolAware
                            ? "Codex-connected tool request failed after continuation fallback"
                            : "Codex-connected request failed after continuation fallback",
                        fallbackResponse,
                        retryResponseContent);
                }

                throw CreateHttpException(
                    toolAware
                        ? "Codex-connected tool request failed after silent refresh"
                        : "Codex-connected request failed after silent refresh",
                    retryResponse,
                    retryResponseContent);
            }

            throw new AgentChatProviderException(
                "codex-auth-required",
                "### Codex authorization required\n\nYour saved Codex session could not be refreshed silently. Open **Settings -> Packages -> Sunder Agent Provider OpenAI**, click **Authorize**, and then retry.",
                "codex-auth-required");
        }

        throw CreateHttpException(
            toolAware ? "Codex-connected tool request failed" : "Codex-connected request failed",
            initialResponse,
            initialResponseContent);
    }

    private static bool IsAuthenticationFailure(HttpStatusCode statusCode, string responseContent)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return true;
        }

        return statusCode == HttpStatusCode.Forbidden
            && (responseContent.Contains("token", StringComparison.OrdinalIgnoreCase)
                || responseContent.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || responseContent.Contains("expired", StringComparison.OrdinalIgnoreCase)
                || responseContent.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<RequestAttempt> SendAsync(
        OpenAiCodexSession session,
        AgentChatClientContext context,
        IReadOnlyList<AIChatMessage> messages,
        ChatOptions? options,
        bool toolAware,
        CodexResponseContinuationStore continuationStore,
        bool disableContinuation,
        CancellationToken cancellationToken)
    {
        var prepareStopwatch = Stopwatch.StartNew();
        var modelId = options?.ModelId ?? context.ModelId;
        var request = CodexResponsesRequestBuilder.Build(
            context,
            messages,
            options,
            toolAware,
            continuationStore.Get(options?.ConversationId),
            disableContinuation);
        await LogAsync(
            context,
            AgentLogLevel.Debug,
            toolAware ? "openai.codex.tool_request.prepared" : "openai.codex.request.prepared",
            "Codex request prepared.",
            prepareStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model.id"] = request.Model,
                ["request.input_item_count"] = request.InputItemCount,
                ["request.conversation_input_item_count"] = request.ConversationInputItemCount,
                ["request.tool_count"] = request.ToolCount,
                ["request.body_length"] = request.Body.Length,
                ["request.uses_developer_instruction_input"] = request.UsesDeveloperInstructionInput,
                ["request.has_top_level_instructions"] = request.HasTopLevelInstructions,
                ["request.service_tier"] = request.ServiceTier,
                ["request.has_prompt_cache_key"] = request.HasPromptCacheKey,
                ["request.has_include_options"] = request.HasIncludeOptions,
                ["request.has_reasoning_options"] = request.HasReasoningOptions,
                ["request.has_text_options"] = request.HasTextOptions,
                ["request.has_previous_response_id"] = request.HasPreviousResponseId,
                ["request.tool_choice"] = request.ToolChoice,
                ["request.parallel_tool_calls"] = request.ParallelToolCalls,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "codex/responses")
        {
            Content = new StringContent(request.Body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        httpRequest.Headers.Add("ChatGPT-Account-Id", session.ChatGptAccountId);
        httpRequest.Headers.Add("originator", "sunder");
        httpRequest.Headers.UserAgent.ParseAdd(UserAgent);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(options?.ConversationId))
        {
            httpRequest.Headers.Add("session_id", options.ConversationId);
            httpRequest.Headers.Add("x-session-affinity", options.ConversationId);
        }

        var sendStopwatch = Stopwatch.StartNew();
        await LogAsync(
            context,
            AgentLogLevel.Debug,
            toolAware ? "openai.codex.tool_http.send.start" : "openai.codex.http.send.start",
            "POST codex/responses",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            },
            cancellationToken: cancellationToken);
        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await LogAsync(
            context,
            response.IsSuccessStatusCode ? AgentLogLevel.Debug : AgentLogLevel.Warning,
            toolAware ? "openai.codex.tool_http.headers_received" : "openai.codex.http.headers_received",
            $"{(int)response.StatusCode} {response.ReasonPhrase}",
            sendStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["http.status_code"] = (int)response.StatusCode,
                ["http.reason_phrase"] = response.ReasonPhrase,
                ["http.response_content_type"] = response.Content.Headers.ContentType?.ToString(),
                ["network.address_family"] = CodexHttpClientFactory.NetworkAddressFamily,
            },
            cancellationToken: cancellationToken);
        return new RequestAttempt(response, request);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ParseAndRecordContinuationAsync(
        HttpResponseMessage response,
        AgentChatClientContext context,
        ChatOptions? options,
        CodexResponsesRequest request,
        CodexResponseContinuationStore continuationStore,
        string responseId,
        string messageId,
        bool toolAware,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? backendResponseId = null;
        var textBuilder = new StringBuilder();
        var functionCalls = new List<FunctionCallContent>();
        await foreach (var update in CodexResponsesStreamParser.ParseAsync(
                           response,
                           context,
                           options,
                           responseId,
                           messageId,
                           toolAware,
                           cancellationToken,
                           observedResponseId => backendResponseId = observedResponseId))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                textBuilder.Append(update.Text);
            }

            foreach (var functionCall in update.Contents.OfType<FunctionCallContent>())
            {
                functionCalls.Add(functionCall);
            }

            yield return update;
        }

        if (backendResponseId is null || string.IsNullOrWhiteSpace(options?.ConversationId))
        {
            yield break;
        }

        var outputFingerprints = CodexResponsesRequestBuilder.BuildAssistantOutputFingerprints(
            textBuilder.Length == 0 ? null : textBuilder.ToString(),
            functionCalls);
        continuationStore.Save(new CodexResponseContinuationState(
            options.ConversationId,
            request.ShapeFingerprint,
            backendResponseId,
            request.ConversationItemFingerprints.Concat(outputFingerprints).ToArray()));
    }

    private static bool IsContinuationFailure(CodexResponsesRequest request, HttpResponseMessage response, string responseContent)
        => request.HasPreviousResponseId
           && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict
           && responseContent.Contains("previous_response", StringComparison.OrdinalIgnoreCase);

    private static ValueTask LogAsync(
        AgentChatClientContext context,
        AgentLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => context.LogProviderEventAsync(level, eventName, message, elapsedMilliseconds, attributes, exception, cancellationToken);

    private static AgentChatProviderException CreateHttpException(string title, HttpResponseMessage response, string responseContent)
        => new(
            "codex-http-error",
            $"### {title}\n\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n```json\n{responseContent}\n```",
            "codex-http-error",
            new HttpRequestException(title, null, response.StatusCode));

    private sealed class RequestAttempt(HttpResponseMessage response, CodexResponsesRequest request) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public CodexResponsesRequest Request { get; } = request;

        public void Dispose()
        {
            Response.Dispose();
        }
    }
}

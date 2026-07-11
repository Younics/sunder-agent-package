using System.Diagnostics;
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
    private static readonly string UserAgent =
        $"Sunder/{typeof(CodexConnectedTransport).Assembly.GetName().Version} ({Environment.OSVersion.Platform}; {Environment.OSVersion.VersionString}; {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
    private readonly CodexConnectedAuthStrategy _codexConnectedAuthStrategy = codexConnectedAuthStrategy;
    private readonly HttpClient _httpClient = httpClient;

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
        var continuation = new CodexContinuationManager(continuationStore, options?.ConversationId);
        var attemptPolicy = new CodexAttemptPolicy(session);

        while (true)
        {
            using var attempt = await SendAsync(
                attemptPolicy.Session,
                context,
                messages,
                options,
                toolAware,
                continuation.State,
                attemptPolicy.DisableContinuation,
                cancellationToken);
            if (attempt.Response.IsSuccessStatusCode)
            {
                await foreach (var update in ParseAndRecordContinuationAsync(
                                   attempt.Response,
                                   context,
                                   options,
                                   attempt.Request,
                                   continuation,
                                   responseId,
                                   messageId,
                                   toolAware,
                                   cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            var responseContent = await attempt.Response.Content.ReadAsStringAsync(cancellationToken);
            switch (attemptPolicy.Decide(attempt.Request, attempt.Response.StatusCode, responseContent))
            {
                case CodexAttemptAction.RetryWithoutContinuation:
                    continuation.Reject();
                    await LogAsync(
                        context,
                        AgentLogLevel.Debug,
                        "openai.codex.continuation.fallback",
                        "Codex response continuation was rejected; retrying with full prompt.",
                        cancellationToken: cancellationToken);
                    continue;

                case CodexAttemptAction.RefreshAuthentication:
                    var refreshStopwatch = Stopwatch.StartNew();
                    await LogAsync(
                        context,
                        AgentLogLevel.Information,
                        "openai.codex.auth_refresh.start",
                        "Refreshing Codex auth session.",
                        cancellationToken: cancellationToken);
                    var refreshedSession = await _codexConnectedAuthStrategy.TryRefreshSessionAsync(
                        attemptPolicy.Session,
                        cancellationToken);
                    await LogAsync(
                        context,
                        AgentLogLevel.Information,
                        "openai.codex.auth_refresh.completed",
                        refreshedSession is null ? "not refreshed" : "refreshed",
                        refreshStopwatch.ElapsedMilliseconds,
                        cancellationToken: cancellationToken);
                    if (refreshedSession is null)
                    {
                        throw CreateAuthRequiredException();
                    }

                    attemptPolicy.ApplyRefreshedSession(refreshedSession);
                    continue;

                case CodexAttemptAction.AuthenticationRequired:
                    throw CreateAuthRequiredException();

                default:
                    throw CreateHttpException(
                        attemptPolicy.BuildFailureTitle(toolAware),
                        attempt.Response,
                        responseContent);
            }
        }
    }

    private async Task<RequestAttempt> SendAsync(
        OpenAiCodexSession session,
        AgentChatClientContext context,
        IReadOnlyList<AIChatMessage> messages,
        ChatOptions? options,
        bool toolAware,
        CodexResponseContinuationState? continuationState,
        bool disableContinuation,
        CancellationToken cancellationToken)
    {
        var prepareStopwatch = Stopwatch.StartNew();
        var request = CodexResponsesRequestBuilder.Build(
            context,
            messages,
            options,
            toolAware,
            continuationState,
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
                ["request.max_output_tokens"] = request.MaxOutputTokens,
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
            attributes: new Dictionary<string, object?>
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
        CodexContinuationManager continuation,
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

            functionCalls.AddRange(update.Contents.OfType<FunctionCallContent>());
            yield return update;
        }

        continuation.RecordCompleted(
            request,
            backendResponseId,
            textBuilder.Length == 0 ? null : textBuilder.ToString(),
            functionCalls);
    }

    private static AgentChatProviderException CreateAuthRequiredException()
        => new(
            "codex-auth-required",
            "### Codex authorization required\n\nYour saved Codex session could not be refreshed silently. Open **Settings -> Packages -> Sunder Agent Provider OpenAI**, click **Authorize**, and then retry.",
            "codex-auth-required");

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

    private static AgentChatProviderException CreateHttpException(
        string title,
        HttpResponseMessage response,
        string responseContent)
        => new(
            "codex-http-error",
            $"### {title}\n\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n```json\n{responseContent}\n```",
            "codex-http-error",
            new HttpRequestException(title, null, response.StatusCode));

    private sealed class RequestAttempt(HttpResponseMessage response, CodexResponsesRequest request) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public CodexResponsesRequest Request { get; } = request;
        public void Dispose() => Response.Dispose();
    }
}

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
        string responseId,
        string messageId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolAware = options?.ToolMode != AIChatToolMode.None && options?.Tools is { Count: > 0 };
        using var initialAttempt = await SendAsync(session, context, messages, options, toolAware, cancellationToken);
        var initialResponse = initialAttempt.Response;
        if (initialResponse.IsSuccessStatusCode)
        {
            await foreach (var update in CodexResponsesStreamParser.ParseAsync(initialResponse, context, options, responseId, messageId, toolAware, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        var initialResponseContent = await initialResponse.Content.ReadAsStringAsync(cancellationToken);
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
                using var retryAttempt = await SendAsync(refreshedSession, context, messages, options, toolAware, cancellationToken);
                var retryResponse = retryAttempt.Response;
                if (retryResponse.IsSuccessStatusCode)
                {
                    await foreach (var update in CodexResponsesStreamParser.ParseAsync(retryResponse, context, options, responseId, messageId, toolAware, cancellationToken))
                    {
                        yield return update;
                    }

                    yield break;
                }

                var retryResponseContent = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var prepareStopwatch = Stopwatch.StartNew();
        var modelId = options?.ModelId ?? context.ModelId;
        var request = CodexResponsesRequestBuilder.Build(context, messages, options, toolAware);
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
                ["request.tool_count"] = request.ToolCount,
                ["request.body_length"] = request.Body.Length,
                ["request.uses_developer_instruction_input"] = request.UsesDeveloperInstructionInput,
                ["request.has_top_level_instructions"] = request.HasTopLevelInstructions,
                ["request.service_tier"] = request.ServiceTier,
                ["request.has_prompt_cache_key"] = request.HasPromptCacheKey,
                ["request.has_include_options"] = request.HasIncludeOptions,
                ["request.has_reasoning_options"] = request.HasReasoningOptions,
                ["request.has_text_options"] = request.HasTextOptions,
                ["request.tool_choice"] = request.ToolChoice,
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
            },
            cancellationToken: cancellationToken);
        return new RequestAttempt(response);
    }

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

    private sealed class RequestAttempt(HttpResponseMessage response) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public void Dispose()
        {
            Response.Dispose();
        }
    }
}

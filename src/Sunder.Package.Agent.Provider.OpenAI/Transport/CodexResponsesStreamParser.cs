using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class CodexResponsesStreamParser
{
    public static async IAsyncEnumerable<ChatResponseUpdate> ParseAsync(
        HttpResponseMessage response,
        AgentChatClientContext context,
        ChatOptions? options,
        string responseId,
        string messageId,
        bool toolAware,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<string>? responseIdObserver = null)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var toolCalls = new CodexStreamingToolCallCollector(options?.AllowMultipleToolCalls == true);
        var modelId = options?.ModelId ?? context.ModelId;
        var currentResponseId = responseId;
        var partialTextCharacters = 0;
        var emittedToolCalls = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw CreateTerminalException(
                    "openai-stream-premature-eof",
                    "OpenAI stream ended before a completed response event.",
                    partialTextCharacters,
                    emittedToolCalls);
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                throw CreateTerminalException(
                    "openai-stream-premature-done",
                    "OpenAI stream sent [DONE] before a completed response event.",
                    partialTextCharacters,
                    emittedToolCalls);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw CreateTerminalException(
                    "openai-stream-malformed-event",
                    "OpenAI stream contained malformed JSON.",
                    partialTextCharacters,
                    emittedToolCalls,
                    innerException: ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (TryGetResponseId(root) is { Length: > 0 } observedResponseId)
                {
                    currentResponseId = observedResponseId;
                    responseIdObserver?.Invoke(observedResponseId);
                }

                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var eventType = typeElement.GetString();
                if (IsReasoningSummaryDeltaEvent(eventType) && TryExtractReasoningDelta(root, out var reasoningDelta))
                {
                    partialTextCharacters += reasoningDelta.Length;
                    yield return new ChatResponseUpdate(AIChatRole.Assistant, [new TextReasoningContent(reasoningDelta)])
                    {
                        ResponseId = currentResponseId,
                        MessageId = messageId,
                        ModelId = modelId,
                    };
                    continue;
                }

                switch (eventType)
                {
                    case "response.output_text.delta":
                        if (TryGetStringProperty(root, "delta", out var delta))
                        {
                            partialTextCharacters += delta.Length;
                            yield return new ChatResponseUpdate(AIChatRole.Assistant, delta)
                            {
                                ResponseId = currentResponseId,
                                MessageId = messageId,
                                ModelId = modelId,
                            };
                        }
                        break;

                    case "response.output_item.added":
                        if (!toolCalls.Register(root))
                        {
                            throw new AgentChatProviderException(
                                "openai-multiple-tool-calls",
                                "### OpenAI requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
                                "openai-multiple-tool-calls");
                        }
                        break;

                    case "response.function_call_arguments.delta":
                        toolCalls.AppendArguments(root);
                        break;

                    case "response.function_call_arguments.done":
                        if (toolCalls.Complete(root) is { } completedToolCall)
                        {
                            emittedToolCalls++;
                            yield return CreateToolCallUpdate(completedToolCall, currentResponseId, messageId, modelId);
                        }
                        break;

                    case "response.completed":
                        EnsureCompletedStatus(root, partialTextCharacters, emittedToolCalls);
                        toolCalls.EnsureAllCompleted();
                        yield break;

                    case "response.failed":
                        throw CreateTerminalException(
                            "openai-stream-failed",
                            BuildTerminalMessage("OpenAI response failed.", root),
                            partialTextCharacters,
                            emittedToolCalls);

                    case "response.incomplete":
                        throw CreateTerminalException(
                            "openai-stream-incomplete",
                            BuildTerminalMessage("OpenAI response was incomplete.", root),
                            partialTextCharacters,
                            emittedToolCalls);

                    case "error":
                        throw CreateTerminalException(
                            "openai-stream-error",
                            BuildTerminalMessage("OpenAI stream ended with an error event.", root),
                            partialTextCharacters,
                            emittedToolCalls);
                }
            }
        }
    }

    private static ChatResponseUpdate CreateToolCallUpdate(
        CodexToolCall toolCall,
        string responseId,
        string messageId,
        string modelId)
        => new(AIChatRole.Assistant, [new FunctionCallContent(
            toolCall.CallId,
            toolCall.ToolId,
            toolCall.ParseArguments())])
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
        };

    private static void EnsureCompletedStatus(JsonElement root, int partialTextCharacters, int emittedToolCalls)
    {
        if (!root.TryGetProperty("response", out var response)
            || response.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(response, "status", out var status)
            || !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateTerminalException(
                "openai-stream-invalid-completion",
                "OpenAI stream sent a completed event without a valid completed status.",
                partialTextCharacters,
                emittedToolCalls);
        }
    }

    private static string BuildTerminalMessage(string prefix, JsonElement root)
    {
        var detail = TryGetNestedString(root, "response", "error", "message")
                     ?? TryGetNestedString(root, "response", "incomplete_details", "reason")
                     ?? TryGetNestedString(root, "error", "message")
                     ?? TryGetString(root, "message");
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix} {detail}";
    }

    private static AgentChatProviderException CreateTerminalException(
        string errorCode,
        string message,
        int partialTextCharacters,
        int emittedToolCalls,
        Exception? innerException = null)
        => new(
            errorCode,
            $"### OpenAI stream did not complete\n\n{message}\n\nPartial output diagnostics: {partialTextCharacters} text characters and {emittedToolCalls} tool calls were emitted before termination.",
            errorCode,
            innerException);

    private static bool IsReasoningSummaryDeltaEvent(string? eventType)
        => !string.IsNullOrWhiteSpace(eventType)
           && eventType.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
           && eventType.Contains("delta", StringComparison.OrdinalIgnoreCase)
           && (eventType.Contains("summary", StringComparison.OrdinalIgnoreCase)
               || eventType.Contains("text", StringComparison.OrdinalIgnoreCase));

    private static bool TryExtractReasoningDelta(JsonElement root, out string delta)
    {
        if (TryGetStringProperty(root, "delta", out delta)
            || TryGetStringProperty(root, "text", out delta))
        {
            return true;
        }

        if (root.TryGetProperty("summary", out var summary))
        {
            return TryGetStringProperty(summary, "text", out delta)
                   || TryGetStringProperty(summary, "delta", out delta);
        }

        delta = string.Empty;
        return false;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? TryGetNestedString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (!element.TryGetProperty(segment, out element) || element.ValueKind != JsonValueKind.Object && segment != path[^1])
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static string? TryGetResponseId(JsonElement root)
    {
        var responseId = TryGetString(root, "response_id");
        if (!string.IsNullOrWhiteSpace(responseId))
        {
            return responseId;
        }

        return root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
            ? TryGetString(response, "id")
            : null;
    }
}

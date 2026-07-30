using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
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
        var lineReader = new BoundedSseLineReader(reader, AgentPayloadLimits.MaxProviderSseLineBytes);
        var toolCalls = new CodexStreamingToolCallCollector(options?.AllowMultipleToolCalls == true);
        var modelId = options?.ModelId ?? context.ModelId;
        var currentResponseId = responseId;
        var partialTextCharacters = 0;
        var emittedToolCalls = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderSseLineLimitException)
            {
                throw CreateTerminalException(
                    "openai-stream-line-too-large",
                    $"OpenAI stream line exceeded the {AgentPayloadLimits.MaxProviderSseLineBytes}-byte limit.",
                    partialTextCharacters,
                    emittedToolCalls);
            }
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
                document = JsonDocument.Parse(payload, new JsonDocumentOptions
                {
                    MaxDepth = AgentPayloadLimits.MaxToolArgumentJsonDepth,
                });
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
                    await new ProviderStreamTelemetry(context).UnsupportedResponseAsync(
                        "OpenAI",
                        ["MissingEventType"],
                        cancellationToken);
                    continue;
                }

                var eventType = typeElement.GetString();
                if (IsReasoningSummaryDeltaEvent(eventType) && TryExtractReasoningDelta(root, out var reasoningDelta))
                {
                    partialTextCharacters += reasoningDelta.Length;
                    yield return ProviderResponseUpdates.Create(
                        modelId,
                        currentResponseId,
                        messageId,
                        new TextReasoningContent(reasoningDelta));
                    continue;
                }

                switch (eventType)
                {
                    case "response.output_text.delta":
                        if (TryGetString(root, "delta") is { Length: > 0 } delta)
                        {
                            partialTextCharacters += delta.Length;
                            yield return ProviderResponseUpdates.CreateText(
                                modelId,
                                currentResponseId,
                                messageId,
                                delta);
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
                        if (TryGetUsage(root, out var usage)
                            && ProviderResponseUpdates.CreateUsage(
                                modelId,
                                currentResponseId,
                                messageId,
                                usage) is { } usageUpdate)
                        {
                            yield return usageUpdate;
                        }
                        yield break;

                    case "response.failed":
                        throw CreateTerminalException(
                            "openai-stream-failed",
                            BuildTerminalMessage("OpenAI response failed.", root),
                            partialTextCharacters,
                            emittedToolCalls,
                            failureKind: ClassifyFailure(root));

                    case "response.incomplete":
                        throw CreateTerminalException(
                            "openai-stream-incomplete",
                            BuildTerminalMessage("OpenAI response was incomplete.", root),
                            partialTextCharacters,
                            emittedToolCalls,
                            failureKind: ClassifyFailure(root));

                    case "error":
                        throw CreateTerminalException(
                            "openai-stream-error",
                            BuildTerminalMessage("OpenAI stream ended with an error event.", root),
                            partialTextCharacters,
                            emittedToolCalls,
                            failureKind: ClassifyFailure(root));
                    default:
                        if (!IsKnownInformationalEvent(eventType))
                        {
                            await new ProviderStreamTelemetry(context).UnsupportedResponseAsync(
                                "OpenAI",
                                [eventType ?? "MissingEventType"],
                                cancellationToken);
                        }
                        break;
                }
            }
        }
    }

    private static ChatResponseUpdate CreateToolCallUpdate(
        CodexToolCall toolCall,
        string responseId,
        string messageId,
        string modelId)
        => ProviderResponseUpdates.Create(
            modelId,
            responseId,
            messageId,
            new FunctionCallContent(
                toolCall.CallId,
                toolCall.ToolId,
                toolCall.ParseArguments()));

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
        Exception? innerException = null,
        AgentChatProviderFailureKind failureKind = AgentChatProviderFailureKind.Unknown)
        => new(
            errorCode,
            $"### OpenAI stream did not complete\n\n{message}\n\nPartial output diagnostics: {partialTextCharacters} text characters and {emittedToolCalls} tool calls were emitted before termination.",
            errorCode,
            innerException)
        {
            FailureKind = failureKind,
        };

    private static AgentChatProviderFailureKind ClassifyFailure(JsonElement root)
    {
        var diagnostic = string.Join(
            ": ",
            new[]
            {
                TryGetNestedString(root, "response", "error", "code"),
                TryGetNestedString(root, "response", "error", "message"),
                TryGetNestedString(root, "error", "code"),
                TryGetNestedString(root, "error", "message"),
                TryGetNestedString(root, "response", "incomplete_details", "reason"),
                TryGetString(root, "code"),
                TryGetString(root, "message"),
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return ProviderContextWindowFailureClassifier.IsOpenAi(diagnostic)
            ? AgentChatProviderFailureKind.ContextWindowExceeded
            : AgentChatProviderFailureKind.Unknown;
    }

    private static bool IsReasoningSummaryDeltaEvent(string? eventType)
        => !string.IsNullOrWhiteSpace(eventType)
           && eventType.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
           && eventType.Contains("delta", StringComparison.OrdinalIgnoreCase)
           && (eventType.Contains("summary", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("text", StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownInformationalEvent(string? eventType)
        => eventType is "response.created"
            or "response.in_progress"
            or "response.output_item.done"
            or "response.content_part.added"
            or "response.content_part.done"
            or "response.output_text.done";

    private static bool TryGetUsage(JsonElement root, out ProviderUsageSnapshot usage)
    {
        usage = default;
        if (!root.TryGetProperty("response", out var response)
            || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("usage", out var usageElement)
            || usageElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var input = GetInt64(usageElement, "input_tokens");
        var output = GetInt64(usageElement, "output_tokens");
        usage = new ProviderUsageSnapshot(
            input,
            output,
            AddIfBothPresent(input, output),
            GetNestedInt64(usageElement, "input_tokens_details", "cached_tokens"),
            GetNestedInt64(usageElement, "output_tokens_details", "reasoning_tokens"));
        return usage.HasValue;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static long? GetNestedInt64(JsonElement element, string objectName, string propertyName)
        => element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetInt64(nested, propertyName)
            : null;

    private static long? AddIfBothPresent(long? left, long? right)
        => left is not null && right is not null ? left + right : null;

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

internal sealed class BoundedSseLineReader(TextReader reader, int maxLineBytes)
{
    private readonly char[] _buffer = new char[4096];
    private int _bufferOffset;
    private int _bufferLength;

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_bufferOffset == _bufferLength)
            {
                _bufferLength = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferLength == 0)
                {
                    return line.Length == 0 ? null : Complete(line);
                }
            }

            var remaining = _buffer.AsSpan(_bufferOffset, _bufferLength - _bufferOffset);
            var newlineIndex = remaining.IndexOf('\n');
            var segment = newlineIndex < 0 ? remaining : remaining[..newlineIndex];
            if (line.Length + segment.Length > maxLineBytes)
            {
                throw new ProviderSseLineLimitException();
            }

            line.Append(segment);
            _bufferOffset += segment.Length;
            if (newlineIndex >= 0)
            {
                _bufferOffset++;
                return Complete(line);
            }
        }
    }

    private string Complete(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        var value = line.ToString();
        if (Encoding.UTF8.GetByteCount(value) > maxLineBytes)
        {
            throw new ProviderSseLineLimitException();
        }

        return value;
    }
}

internal sealed class ProviderSseLineLimitException : Exception;

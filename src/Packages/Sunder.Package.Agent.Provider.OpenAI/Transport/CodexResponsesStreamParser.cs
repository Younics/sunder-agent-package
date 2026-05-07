using System.Runtime.CompilerServices;
using System.Text;
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var toolCallsByIndex = new Dictionary<int, StreamingToolCallAccumulator>();
        var modelId = options?.ModelId ?? context.ModelId;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (typeElement.GetString())
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var deltaElement) && deltaElement.ValueKind == JsonValueKind.String)
                    {
                        var delta = deltaElement.GetString() ?? string.Empty;
                        yield return new ChatResponseUpdate(AIChatRole.Assistant, delta)
                        {
                            ResponseId = responseId,
                            MessageId = messageId,
                            ModelId = modelId,
                        };
                    }
                    break;

                case "response.output_item.added":
                    RegisterStreamingToolCall(root, toolCallsByIndex, out var tooManyToolCalls);
                    if (tooManyToolCalls)
                    {
                        throw new AgentChatProviderException(
                            "openai-multiple-tool-calls",
                            "### OpenAI requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
                            "openai-multiple-tool-calls");
                    }
                    break;

                case "response.function_call_arguments.delta":
                    AppendStreamingToolCallDelta(root, toolCallsByIndex);
                    break;

                case "response.function_call_arguments.done":
                    if (TryBuildCompletedStreamingToolCall(root, toolCallsByIndex, out var completedToolCall))
                    {
                        yield return CreateToolCallUpdate(completedToolCall!, responseId, messageId, modelId);
                        yield break;
                    }
                    break;

                case "response.completed":
                    yield break;

                case "error":
                    throw new AgentChatProviderException(
                        "openai-stream-error",
                        "### OpenAI stream error\n\nThe stream ended with an error event.",
                        "openai-stream-error");
            }
        }

    }

    private static ChatResponseUpdate CreateToolCallUpdate(CodexToolCall toolCall, string responseId, string messageId, string modelId)
        => new(AIChatRole.Assistant, [new FunctionCallContent(toolCall.CallId, toolCall.ToolId, ParseArguments(toolCall.ArgumentsJson))])
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
        };

    private static IDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = document.RootElement.Clone(),
                };
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }

            return arguments;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = argumentsJson,
            };
        }
    }

    private static void RegisterStreamingToolCall(
        JsonElement root,
        Dictionary<int, StreamingToolCallAccumulator> toolCallsByIndex,
        out bool tooManyToolCalls)
    {
        tooManyToolCalls = false;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!string.Equals(TryGetString(item, "type"), "function_call", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var outputIndex = TryGetInt32(root, "output_index");
        if (outputIndex < 0)
        {
            return;
        }

        if (!toolCallsByIndex.TryGetValue(outputIndex, out var accumulator))
        {
            if (toolCallsByIndex.Count > 0)
            {
                tooManyToolCalls = true;
                return;
            }

            accumulator = new StreamingToolCallAccumulator(outputIndex);
            toolCallsByIndex[outputIndex] = accumulator;
        }

        accumulator.CallId ??= TryGetString(item, "call_id") ?? TryGetString(root, "call_id") ?? TryGetString(item, "id");
        accumulator.ToolId ??= TryGetString(item, "name") ?? TryGetString(root, "name");
    }

    private static void AppendStreamingToolCallDelta(JsonElement root, Dictionary<int, StreamingToolCallAccumulator> toolCallsByIndex)
    {
        var outputIndex = TryGetInt32(root, "output_index");
        if (outputIndex < 0 || !toolCallsByIndex.TryGetValue(outputIndex, out var accumulator))
        {
            return;
        }

        accumulator.CallId ??= TryGetString(root, "call_id");
        if (root.TryGetProperty("delta", out var deltaElement) && deltaElement.ValueKind == JsonValueKind.String)
        {
            accumulator.ArgumentsBuilder.Append(deltaElement.GetString());
        }
    }

    private static bool TryBuildCompletedStreamingToolCall(
        JsonElement root,
        Dictionary<int, StreamingToolCallAccumulator> toolCallsByIndex,
        out CodexToolCall? toolCall)
    {
        toolCall = null;

        var outputIndex = TryGetInt32(root, "output_index");
        if (outputIndex < 0 || !toolCallsByIndex.TryGetValue(outputIndex, out var accumulator))
        {
            return false;
        }

        accumulator.CallId ??= TryGetString(root, "call_id");
        accumulator.ToolId ??= TryGetString(root, "name");

        var arguments = TryGetString(root, "arguments");
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            accumulator.ArgumentsBuilder.Clear();
            accumulator.ArgumentsBuilder.Append(arguments);
        }

        if (string.IsNullOrWhiteSpace(accumulator.ToolId))
        {
            return false;
        }

        toolCall = new CodexToolCall(
            accumulator.CallId ?? Guid.NewGuid().ToString("N"),
            accumulator.ToolId,
            accumulator.ArgumentsBuilder.Length == 0 ? "{}" : accumulator.ArgumentsBuilder.ToString());
        return true;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int TryGetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : -1;

    private sealed class StreamingToolCallAccumulator(int outputIndex)
    {
        public int OutputIndex { get; } = outputIndex;

        public string? CallId { get; set; }

        public string? ToolId { get; set; }

        public StringBuilder ArgumentsBuilder { get; } = new();
    }

    private sealed record CodexToolCall(string CallId, string ToolId, string ArgumentsJson);
}

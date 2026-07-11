using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal sealed class CodexStreamingToolCallCollector(bool allowMultipleToolCalls)
{
    private readonly bool _allowMultipleToolCalls = allowMultipleToolCalls;
    private readonly Dictionary<int, ToolCallAccumulator> _toolCallsByIndex = [];

    public bool Register(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item)
            || item.ValueKind != JsonValueKind.Object
            || !string.Equals(TryGetString(item, "type"), "function_call", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var outputIndex = TryGetInt32(root, "output_index");
        if (outputIndex < 0)
        {
            throw Malformed("OpenAI started a function call without a valid output index.");
        }

        if (!_toolCallsByIndex.TryGetValue(outputIndex, out var accumulator))
        {
            if (!_allowMultipleToolCalls && _toolCallsByIndex.Count > 0)
            {
                return false;
            }

            accumulator = new ToolCallAccumulator();
            _toolCallsByIndex[outputIndex] = accumulator;
        }

        accumulator.CallId ??= TryGetString(item, "call_id") ?? TryGetString(root, "call_id") ?? TryGetString(item, "id");
        accumulator.ToolId ??= TryGetString(item, "name") ?? TryGetString(root, "name");
        return true;
    }

    public void AppendArguments(JsonElement root)
    {
        var outputIndex = TryGetInt32(root, "output_index");
        if (outputIndex < 0 || !_toolCallsByIndex.TryGetValue(outputIndex, out var accumulator))
        {
            throw Malformed("OpenAI streamed function arguments before starting the function call.");
        }

        accumulator.CallId ??= TryGetString(root, "call_id");
        if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
        {
            accumulator.Arguments.Append(delta.GetString());
        }
    }

    public CodexToolCall? Complete(JsonElement root)
    {
        var outputIndex = TryGetInt32(root, "output_index");
        if (outputIndex < 0
            || !_toolCallsByIndex.TryGetValue(outputIndex, out var accumulator)
            || accumulator.Completed)
        {
            throw Malformed("OpenAI completed an unknown or already-completed function call.");
        }

        accumulator.CallId ??= TryGetString(root, "call_id");
        accumulator.ToolId ??= TryGetString(root, "name");
        var arguments = TryGetString(root, "arguments");
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            accumulator.Arguments.Clear();
            accumulator.Arguments.Append(arguments);
        }

        if (string.IsNullOrWhiteSpace(accumulator.ToolId))
        {
            throw Malformed("OpenAI completed a function call without a function name.");
        }

        accumulator.Completed = true;
        return new CodexToolCall(
            accumulator.CallId ?? Guid.NewGuid().ToString("N"),
            accumulator.ToolId,
            accumulator.Arguments.Length == 0 ? "{}" : accumulator.Arguments.ToString());
    }

    public void EnsureAllCompleted()
    {
        if (_toolCallsByIndex.Values.Any(toolCall => !toolCall.Completed))
        {
            throw Malformed("OpenAI completed the response while a function call was still unfinished.");
        }
    }

    private static AgentChatProviderException Malformed(string detail)
        => new(
            detail,
            $"### OpenAI returned a malformed tool call\n\n{detail}",
            "openai-malformed-tool-call");

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

    private sealed class ToolCallAccumulator
    {
        public string? CallId { get; set; }
        public string? ToolId { get; set; }
        public StringBuilder Arguments { get; } = new();
        public bool Completed { get; set; }
    }
}

internal sealed record CodexToolCall(string CallId, string ToolId, string ArgumentsJson)
{
    public IDictionary<string, object?> ParseArguments()
        => ProviderJson.TryParseObjectArguments(ArgumentsJson, out var arguments)
            ? arguments
            : throw new AgentChatProviderException(
                $"OpenAI returned non-object or malformed arguments for tool '{ToolId}'.",
                $"### OpenAI returned a malformed tool call\n\nArguments for `{ToolId}` were not a valid JSON object.",
                "openai-malformed-tool-call");
}

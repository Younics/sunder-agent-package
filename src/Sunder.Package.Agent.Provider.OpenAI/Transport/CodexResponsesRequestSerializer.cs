using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class CodexResponsesRequestSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CodexResponsesRequest Serialize(
        CodexNormalizedResponsesRequest normalized,
        CodexResponseContinuationState? continuationState,
        bool disableContinuation)
    {
        var conversationItemFingerprints = BuildItemFingerprints(normalized.Input);
        var shapeFingerprint = BuildItemFingerprint(new CodexContinuationShape(
            normalized.Model,
            normalized.Instructions,
            normalized.Tools,
            DescribeToolChoice(normalized.ToolChoice),
            normalized.ParallelToolCalls,
            normalized.Include,
            normalized.ServiceTier,
            normalized.MaxOutputTokens,
            normalized.Reasoning,
            normalized.Text));
        var previousResponseId = TryBuildContinuationInput(
            continuationState,
            shapeFingerprint,
            conversationItemFingerprints,
            normalized.Input,
            disableContinuation,
            out var requestInput)
            ? continuationState!.ResponseId
            : null;
        var body = new CodexResponsesRequestBody
        {
            Model = normalized.Model,
            Input = requestInput,
            Instructions = normalized.Instructions,
            Tools = normalized.ToolChoice is null ? null : normalized.Tools,
            ToolChoice = normalized.ToolChoice,
            ParallelToolCalls = normalized.ParallelToolCalls,
            Stream = true,
            Store = false,
            PreviousResponseId = previousResponseId,
            PromptCacheKey = normalized.PromptCacheKey,
            Include = normalized.Include,
            ServiceTier = normalized.ServiceTier,
            MaxOutputTokens = normalized.MaxOutputTokens,
            Reasoning = normalized.Reasoning,
            Text = normalized.Text,
        };

        return new CodexResponsesRequest(
            normalized.Model,
            requestInput.Count,
            normalized.Input.Count,
            normalized.Tools.Count,
            JsonSerializer.Serialize(body, JsonOptions),
            normalized.UsesDeveloperInstructionInput,
            normalized.Instructions is not null,
            normalized.ServiceTier,
            normalized.MaxOutputTokens,
            !string.IsNullOrWhiteSpace(normalized.PromptCacheKey),
            normalized.Include is { Count: > 0 },
            normalized.Reasoning is not null,
            normalized.Text is not null,
            DescribeToolChoice(normalized.ToolChoice),
            normalized.ParallelToolCalls,
            previousResponseId is not null,
            shapeFingerprint,
            conversationItemFingerprints);
    }

    public static IReadOnlyList<string> BuildItemFingerprints(IEnumerable<object> items)
        => items.Select(BuildItemFingerprint).ToArray();

    private static bool TryBuildContinuationInput(
        CodexResponseContinuationState? continuationState,
        string shapeFingerprint,
        IReadOnlyList<string> conversationItemFingerprints,
        IReadOnlyList<object> input,
        bool disableContinuation,
        out IReadOnlyList<object> requestInput)
    {
        requestInput = input;
        if (disableContinuation
            || continuationState is null
            || !string.Equals(continuationState.ShapeFingerprint, shapeFingerprint, StringComparison.Ordinal)
            || continuationState.ConversationItemFingerprints.Count >= conversationItemFingerprints.Count)
        {
            return false;
        }

        for (var index = 0; index < continuationState.ConversationItemFingerprints.Count; index++)
        {
            if (!string.Equals(
                    continuationState.ConversationItemFingerprints[index],
                    conversationItemFingerprints[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        requestInput = input.Skip(continuationState.ConversationItemFingerprints.Count).ToArray();
        return requestInput.Count > 0;
    }

    private static string BuildItemFingerprint(object item)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions))));

    private sealed class CodexResponsesRequestBody
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required IReadOnlyList<object> Input { get; init; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<object>? Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        public object? ToolChoice { get; init; }

        [JsonPropertyName("parallel_tool_calls")]
        public bool? ParallelToolCalls { get; init; }

        [JsonPropertyName("stream")]
        public required bool Stream { get; init; }

        [JsonPropertyName("store")]
        public required bool Store { get; init; }

        [JsonPropertyName("previous_response_id")]
        public string? PreviousResponseId { get; init; }

        [JsonPropertyName("prompt_cache_key")]
        public string? PromptCacheKey { get; init; }

        [JsonPropertyName("include")]
        public IReadOnlyList<string>? Include { get; init; }

        [JsonPropertyName("service_tier")]
        public string? ServiceTier { get; init; }

        [JsonPropertyName("max_output_tokens")]
        public int? MaxOutputTokens { get; init; }

        [JsonPropertyName("reasoning")]
        public CodexReasoningOptions? Reasoning { get; init; }

        [JsonPropertyName("text")]
        public CodexTextOptions? Text { get; init; }
    }

    private sealed record CodexContinuationShape(
        string Model,
        string? Instructions,
        IReadOnlyList<object> Tools,
        object? ToolChoice,
        bool? ParallelToolCalls,
        IReadOnlyList<string>? Include,
        string? ServiceTier,
        int? MaxOutputTokens,
        CodexReasoningOptions? Reasoning,
        CodexTextOptions? Text);

    private static string? DescribeToolChoice(object? toolChoice)
        => toolChoice switch
        {
            string value => value,
            CodexRequiredFunctionToolChoice required => $"function:{required.Name}",
            _ => null,
        };
}

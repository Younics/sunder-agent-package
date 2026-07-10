using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.OpenAI;

#pragma warning disable OPENAI001, SCME0001

internal sealed class OpenAiModelOptionsChatClient(IChatClient inner) : IChatClient
{
    private readonly IChatClient _inner = inner;

    public ChatClientMetadata Metadata { get; } =
        inner.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata ?? new ChatClientMetadata("OpenAI");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, PrepareOptions(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, PrepareOptions(options), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private static ChatOptions? PrepareOptions(ChatOptions? options)
    {
        var speedOptionId = GetOption(options, AgentChatModelOptionKeys.SpeedOptionId);
        var modeOptionId = GetOption(options, AgentChatModelOptionKeys.ModeOptionId);
        if (!string.Equals(speedOptionId, "fast", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(modeOptionId, "pro", StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        var preparedOptions = options?.Clone() ?? new ChatOptions();
        var rawRepresentationFactory = preparedOptions.RawRepresentationFactory;
        preparedOptions.RawRepresentationFactory = client => CreateRawOptions(
            rawRepresentationFactory?.Invoke(client) as CreateResponseOptions,
            GetOption(preparedOptions, AgentChatModelOptionKeys.SpeedOptionId),
            GetOption(preparedOptions, AgentChatModelOptionKeys.ModeOptionId),
            preparedOptions.Reasoning);
        return preparedOptions;
    }

    private static CreateResponseOptions CreateRawOptions(
        CreateResponseOptions? rawOptions,
        string? speedOptionId,
        string? modeOptionId,
        ReasoningOptions? reasoning)
    {
        rawOptions ??= new CreateResponseOptions();
        if (string.Equals(speedOptionId, "fast", StringComparison.OrdinalIgnoreCase))
        {
            rawOptions.ServiceTier = new ResponseServiceTier("priority");
        }

        if (string.Equals(modeOptionId, "pro", StringComparison.OrdinalIgnoreCase))
        {
            var responseReasoning = rawOptions.ReasoningOptions ?? new ResponseReasoningOptions();
            responseReasoning.ReasoningEffortLevel ??= ToOpenAiReasoningEffort(reasoning?.Effort);
            responseReasoning.ReasoningSummaryVerbosity ??= ToOpenAiReasoningSummary(reasoning?.Output);
            responseReasoning.Patch.Set("$.mode"u8, "pro");
            rawOptions.ReasoningOptions = responseReasoning;
        }

        return rawOptions;
    }

    private static ResponseReasoningEffortLevel? ToOpenAiReasoningEffort(ReasoningEffort? effort)
        => effort switch
        {
            ReasoningEffort.None => ResponseReasoningEffortLevel.None,
            ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReasoningEffort.High => ResponseReasoningEffortLevel.High,
            ReasoningEffort.ExtraHigh => new ResponseReasoningEffortLevel("xhigh"),
            _ => null,
        };

    private static ResponseReasoningSummaryVerbosity? ToOpenAiReasoningSummary(ReasoningOutput? output)
        => output switch
        {
            ReasoningOutput.Summary => ResponseReasoningSummaryVerbosity.Concise,
            ReasoningOutput.Full => ResponseReasoningSummaryVerbosity.Detailed,
            _ => null,
        };

    private static string? GetOption(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } properties
        && properties.TryGetValue(key, out var value)
            ? value as string
            : null;
}

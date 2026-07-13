using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Provider.Shared;

internal readonly record struct ProviderUsageSnapshot(
    long? InputTokenCount,
    long? OutputTokenCount,
    long? TotalTokenCount = null,
    long? CachedInputTokenCount = null,
    long? ReasoningTokenCount = null)
{
    public bool HasValue => InputTokenCount is not null
                            || OutputTokenCount is not null
                            || TotalTokenCount is not null
                            || CachedInputTokenCount is not null
                            || ReasoningTokenCount is not null;
}

internal sealed class ProviderUsageAccumulator
{
    private ProviderUsageSnapshot _latest;

    public void SetLatest(ProviderUsageSnapshot usage)
    {
        if (!usage.HasValue)
        {
            return;
        }

        _latest = new ProviderUsageSnapshot(
            usage.InputTokenCount ?? _latest.InputTokenCount,
            usage.OutputTokenCount ?? _latest.OutputTokenCount,
            usage.TotalTokenCount ?? _latest.TotalTokenCount,
            usage.CachedInputTokenCount ?? _latest.CachedInputTokenCount,
            usage.ReasoningTokenCount ?? _latest.ReasoningTokenCount);
    }

    public UsageContent? CreateContent()
        => _latest.HasValue ? ProviderResponseUpdates.CreateUsageContent(_latest) : null;
}

internal static class ProviderResponseUpdates
{
    public static ChatResponseUpdate Create(
        string modelId,
        string responseId,
        string messageId,
        AIContent content,
        ChatFinishReason? finishReason = null)
        => Create(modelId, responseId, messageId, [content], finishReason);

    public static ChatResponseUpdate Create(
        string modelId,
        string responseId,
        string messageId,
        IEnumerable<AIContent> contents,
        ChatFinishReason? finishReason = null)
        => new(ChatRole.Assistant, contents.ToList())
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
            FinishReason = finishReason,
        };

    public static ChatResponseUpdate CreateText(
        string modelId,
        string responseId,
        string messageId,
        string text)
        => new(ChatRole.Assistant, text)
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
        };

    public static ChatResponseUpdate? CreateUsage(
        string modelId,
        string responseId,
        string messageId,
        ProviderUsageSnapshot usage)
        => usage.HasValue
            ? Create(modelId, responseId, messageId, CreateUsageContent(usage))
            : null;

    public static UsageContent CreateUsageContent(ProviderUsageSnapshot usage)
        => new(new UsageDetails
        {
            InputTokenCount = usage.InputTokenCount,
            OutputTokenCount = usage.OutputTokenCount,
            TotalTokenCount = usage.TotalTokenCount
                              ?? AddIfBothPresent(usage.InputTokenCount, usage.OutputTokenCount),
            CachedInputTokenCount = usage.CachedInputTokenCount,
            ReasoningTokenCount = usage.ReasoningTokenCount,
        });

    public static string Describe(ChatResponseUpdate update)
        => Describe(update.Contents);

    public static string Describe(IEnumerable<AIContent> contents)
    {
        var contentList = contents as IReadOnlyList<AIContent> ?? contents.ToArray();
        var content = contentList.FirstOrDefault(item => item is not UsageContent);
        return content switch
        {
            TextReasoningContent => "ReasoningDelta",
            FunctionCallContent => "ToolCallRequested",
            TextContent => "TextDelta",
            null when contentList.OfType<UsageContent>().Any() => "Usage",
            _ => "Update",
        };
    }

    private static long? AddIfBothPresent(long? left, long? right)
        => left is not null && right is not null ? left + right : null;
}

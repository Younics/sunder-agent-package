namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentMemoryRecallPlan(
    AgentMemoryRecallIntent Intent,
    string QueryText,
    string? Reason = null,
    IReadOnlyList<string>? PreferredCategories = null,
    int MaxEntryCount = 6,
    int MaxChars = 2000)
{
    public bool ShouldRecall => Intent != AgentMemoryRecallIntent.None;

    public static AgentMemoryRecallPlan None(string? reason = null)
        => new(AgentMemoryRecallIntent.None, string.Empty, reason, PreferredCategories: null, MaxEntryCount: 0, MaxChars: 0);
}

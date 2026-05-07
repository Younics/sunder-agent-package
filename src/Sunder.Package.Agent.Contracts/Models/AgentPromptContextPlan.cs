namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPromptContextPlan(
    string Intent,
    string QueryText,
    string? Reason = null,
    IReadOnlyList<string>? PreferredCategories = null,
    int MaxEntryCount = 6,
    int MaxChars = 2000)
{
    public bool ShouldContribute => !string.Equals(Intent, "none", StringComparison.OrdinalIgnoreCase);

    public static AgentPromptContextPlan None(string? reason = null)
        => new("none", string.Empty, reason, PreferredCategories: null, MaxEntryCount: 0, MaxChars: 0);
}

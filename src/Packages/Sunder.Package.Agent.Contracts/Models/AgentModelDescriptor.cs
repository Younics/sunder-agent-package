namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentModelDescriptor(
    string ModelId,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool IsRecommended = false,
    IReadOnlyList<AgentModelVariantDescriptor>? Variants = null);

public sealed record AgentModelVariantDescriptor(
    string VariantId,
    string DisplayName,
    string? Description = null,
    AgentReasoningEffort? ReasoningEffort = null);

public enum AgentReasoningEffort
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    ExtraHigh = 4,
}

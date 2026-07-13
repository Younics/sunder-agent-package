namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentModelDescriptor(
    string ModelId,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool IsRecommended = false,
    IReadOnlyList<AgentModelVariantDescriptor>? Variants = null,
    IReadOnlyList<AgentModelSpeedOptionDescriptor>? SpeedOptions = null,
    IReadOnlyList<AgentModelModeOptionDescriptor>? ModeOptions = null)
{
    public DateOnly? ReleaseDate { get; init; }
}

public sealed record AgentModelVariantDescriptor(
    string VariantId,
    string DisplayName,
    string? Description = null,
    AgentReasoningEffort? ReasoningEffort = null);

public sealed record AgentModelSpeedOptionDescriptor(
    string SpeedOptionId,
    string DisplayName,
    string? Description = null);

public sealed record AgentModelModeOptionDescriptor(
    string ModeOptionId,
    string DisplayName,
    string? Description = null,
    bool DisablesReasoning = false);

public enum AgentReasoningEffort
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    ExtraHigh = 4,
}

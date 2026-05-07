namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProfileRecord(
    string ProfileId,
    string DisplayName,
    string? Description,
    string? Instructions,
    string? ChatProviderId,
    string? ChatModelId,
    string? EmbeddingProviderId,
    string? EmbeddingModelId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AgentProfileModelBindingRecord>? ModelBindings = null,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? SelectableCapabilityAssignments = null,
    string? BehaviorLoopId = null,
    string? BehaviorLoopSourceId = null,
    string? BehaviorLoopSettingsJson = null,
    bool IsInternal = false);

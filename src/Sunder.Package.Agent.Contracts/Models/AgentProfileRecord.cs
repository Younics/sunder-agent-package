namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents an immutable persisted snapshot of an Agent profile and its runtime selections.
/// </summary>
/// <remarks>
/// Collection references are retained rather than cloned and must be treated as immutable. Provider, model,
/// behavior-loop, and capability identifiers are opaque extension identifiers. JSON fields are not validated by
/// this record; the selected extension defines their schema and may reject malformed or unsupported settings.
/// </remarks>
/// <param name="ProfileId">The opaque, stable profile identifier.</param>
/// <param name="DisplayName">The user-facing profile name.</param>
/// <param name="Description">An optional user-facing explanation of the profile's purpose.</param>
/// <param name="Instructions">Optional trusted profile instructions composed into the system-instruction channel by the base host.</param>
/// <param name="ChatProviderId">The selected chat provider identifier, or <see langword="null"/> when unconfigured.</param>
/// <param name="ChatModelId">The selected provider-specific chat model identifier, or <see langword="null"/> when unconfigured.</param>
/// <param name="EmbeddingProviderId">The selected embedding provider identifier, or <see langword="null"/> when unconfigured.</param>
/// <param name="EmbeddingModelId">The selected provider-specific embedding model identifier, or <see langword="null"/> when unconfigured.</param>
/// <param name="CreatedAtUtc">The UTC time at which the profile was created.</param>
/// <param name="UpdatedAtUtc">The UTC time of the latest persisted profile change.</param>
/// <param name="ModelBindings">Capability-specific model bindings. Chat and embedding entries mirror the dedicated compatibility fields in the built-in store.</param>
/// <param name="SelectableCapabilityAssignments">Profile-selected extension capabilities, or <see langword="null"/> when none were projected.</param>
/// <param name="BehaviorLoopId">The selected behavior-loop identifier; <see langword="null"/> resolves to the default loop.</param>
/// <param name="BehaviorLoopSourceId">An optional package/source identifier used to disambiguate behavior loops with the same loop identifier.</param>
/// <param name="BehaviorLoopSettingsJson">Optional settings JSON interpreted according to the selected behavior loop's schema.</param>
/// <param name="IsInternal">Whether the profile is runtime-managed and omitted from the built-in public profile listing, such as a generated child-run profile.</param>
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

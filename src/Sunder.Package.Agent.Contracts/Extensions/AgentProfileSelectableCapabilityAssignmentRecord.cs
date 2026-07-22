namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies one selectable capability assigned to an agent profile or subagent.
/// </summary>
/// <remarks>
/// The logical identity is the ordinal case-insensitive tuple (kind, source identifier, capability identifier).
/// Persisted assignments are selection and routing data, not proof that a package remains installed, that the
/// capability is ready, or that its use is authorized. Consumers must resolve the current descriptor and repeat
/// readiness, scope, and permission checks at use time. The record contains no ownership transfer or secret data.
/// </remarks>
/// <param name="Kind">
/// The stable capability category, such as a value from <see cref="AgentProfileSelectableCapabilityKinds"/>.
/// Matching is ordinal case-insensitive; the value should be non-empty and trimmed before persistence.
/// </param>
/// <param name="CapabilityId">
/// The stable capability identifier within the kind and source. It is opaque to generic editors and matched
/// ordinally without regard to case.
/// </param>
/// <param name="SourceId">
/// The stable provider or source identity used to disambiguate equal capability identifiers, or
/// <see langword="null"/> for an intentionally unscoped assignment. Unscoped matching can be broader and must
/// not be treated as authorization.
/// </param>
public sealed record AgentProfileSelectableCapabilityAssignmentRecord(
    string Kind,
    string CapabilityId,
    string? SourceId = null);

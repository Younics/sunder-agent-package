namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents a persisted routing record from a workspace role to one RPC-discovered capability implementation.
/// </summary>
/// <remarks>
/// A binding selects configuration and an implementation; it does not establish user permission to execute that implementation. Identifiers
/// are opaque routing keys and should be compared using the semantics of the owning protocol rather than parsed by consumers.
/// </remarks>
/// <param name="BindingId">
/// The stable identity of this binding and the key normally used for binding-scoped configuration. It is distinct from the target implementation identity.
/// </param>
/// <param name="WorkspaceId">The identity of the workspace that owns the binding.</param>
/// <param name="ExtensionPointId">The RPC contract or capability identity in which <paramref name="ContributionId"/> is resolved.</param>
/// <param name="ContributionId">
/// The selected implementation identity. For an execution-target binding this normally matches <see cref="AgentExecutionTargetDescriptor.TargetId"/>.
/// </param>
/// <param name="Role">The semantic slot filled by the implementation, such as the workspace's primary execution target.</param>
/// <param name="IsEnabled">Whether the binding is eligible for resolution. Disabled bindings must not be used to execute operations.</param>
/// <param name="SortOrder">The relative ordering among bindings when a role permits multiple implementations; lower values are considered first.</param>
/// <param name="CreatedAtUtc">The UTC instant at which the persisted binding was created.</param>
/// <param name="UpdatedAtUtc">The UTC instant at which the persisted binding was last changed.</param>
public sealed record AgentWorkspaceBindingRecord(
    string BindingId,
    string WorkspaceId,
    string ExtensionPointId,
    string ContributionId,
    string Role,
    bool IsEnabled,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

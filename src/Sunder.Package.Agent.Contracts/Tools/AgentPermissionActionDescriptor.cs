namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes a stable permission-controlled action and its resource boundaries.
/// </summary>
/// <remarks>Identifiers are persisted in overrides and pending approvals and must remain stable across package updates.</remarks>
/// <param name="ActionId">The stable action identifier, unique across permission surfaces.</param>
/// <param name="DisplayName">The action name shown in permission settings.</param>
/// <param name="Description">A user-facing explanation of the operation being controlled.</param>
/// <param name="Boundaries">The stable, non-overlapping resource classifications supported by this action.</param>
public sealed record AgentPermissionActionDescriptor(
    string ActionId,
    string DisplayName,
    string Description,
    IReadOnlyList<AgentPermissionBoundaryDescriptor> Boundaries);

/// <summary>
/// Describes one security boundary within a permission action.
/// </summary>
/// <param name="BoundaryId">The stable identifier, unique within its action.</param>
/// <param name="DisplayName">The boundary name shown to users.</param>
/// <param name="Description">A user-facing explanation of what resources fall inside the boundary.</param>
/// <param name="DefaultDecision">The fail-safe decision used when no persisted override or session approval applies.</param>
public sealed record AgentPermissionBoundaryDescriptor(
    string BoundaryId,
    string DisplayName,
    string Description,
    AgentPermissionDecision DefaultDecision);

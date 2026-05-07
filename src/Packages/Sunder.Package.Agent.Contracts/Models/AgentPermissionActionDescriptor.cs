namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPermissionActionDescriptor(
    string ActionId,
    string DisplayName,
    string Description,
    IReadOnlyList<AgentPermissionBoundaryDescriptor> Boundaries);

public sealed record AgentPermissionBoundaryDescriptor(
    string BoundaryId,
    string DisplayName,
    string Description,
    AgentPermissionDecision DefaultDecision);

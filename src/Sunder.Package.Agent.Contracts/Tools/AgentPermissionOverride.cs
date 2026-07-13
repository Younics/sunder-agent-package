namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPermissionOverride(
    string ActionId,
    string BoundaryId,
    AgentPermissionDecision Decision,
    DateTimeOffset UpdatedAtUtc);

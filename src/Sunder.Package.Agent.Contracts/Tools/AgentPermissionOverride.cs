namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Stores a user-configured decision for one stable permission action and boundary pair.
/// </summary>
/// <param name="ActionId">The action identifier published by a permission surface.</param>
/// <param name="BoundaryId">The boundary identifier within that action.</param>
/// <param name="Decision">The configured decision that replaces the boundary default.</param>
/// <param name="UpdatedAtUtc">The UTC time at which the persisted override was last written.</param>
public sealed record AgentPermissionOverride(
    string ActionId,
    string BoundaryId,
    AgentPermissionDecision Decision,
    DateTimeOffset UpdatedAtUtc);

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures the effective policy decision and its audit explanation for one permission request.
/// </summary>
/// <param name="Decision">The effective action after defaults, persisted overrides, and session-scoped approvals are considered.</param>
/// <param name="Reason">A user-facing and audit-safe explanation of how the decision was reached.</param>
/// <param name="Override">The persisted action/boundary override consulted, or <see langword="null" /> when the default applied.</param>
public sealed record AgentPermissionEvaluation(
    AgentPermissionDecision Decision,
    string Reason,
    AgentPermissionOverride? Override = null);

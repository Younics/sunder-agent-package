namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPermissionEvaluation(
    AgentPermissionDecision Decision,
    string Reason,
    AgentPermissionOverride? Override = null);

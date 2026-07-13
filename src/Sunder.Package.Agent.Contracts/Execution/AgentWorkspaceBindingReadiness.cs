namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceBindingReadiness(
    string BindingId,
    AgentExecutionTargetReadinessStatus Status,
    string Message);

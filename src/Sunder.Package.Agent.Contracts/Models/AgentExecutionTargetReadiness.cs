namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentExecutionTargetReadiness(
    string TargetKind,
    string TargetId,
    AgentExecutionTargetReadinessStatus Status,
    string Message);

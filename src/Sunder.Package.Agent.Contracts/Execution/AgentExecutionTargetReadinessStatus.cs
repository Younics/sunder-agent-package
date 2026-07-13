namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentExecutionTargetReadinessStatus
{
    Ready = 0,
    NeedsConfiguration = 1,
    Degraded = 2,
    Failed = 3,
}

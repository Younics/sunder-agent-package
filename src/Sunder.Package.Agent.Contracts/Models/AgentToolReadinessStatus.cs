namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentToolReadinessStatus
{
    Ready = 0,
    NeedsConfiguration = 1,
    Unavailable = 2,
    Failed = 3,
}

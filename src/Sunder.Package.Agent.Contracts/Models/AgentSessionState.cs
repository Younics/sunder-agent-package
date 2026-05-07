namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentSessionState
{
    Active = 0,
    Interrupted = 1,
    Stopped = 2,
    Completed = 3,
    Archived = 4,
    Failed = 5,
}

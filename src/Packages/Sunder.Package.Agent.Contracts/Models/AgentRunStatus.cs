namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentRunStatus
{
    Idle = 0,
    Running = 1,
    Interrupted = 2,
    Stopped = 3,
    Completed = 4,
    Failed = 5,
    WaitingForApproval = 6,
}

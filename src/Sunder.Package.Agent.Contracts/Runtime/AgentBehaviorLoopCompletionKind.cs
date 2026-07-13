namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentBehaviorLoopCompletionKind
{
    Completed = 0,
    Failed = 1,
    WaitingForApproval = 2,
    Stopped = 3,
    Interrupted = 4,
}

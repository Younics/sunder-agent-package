namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentLifecycleEventKind
{
    UserTurnAdded = 0,
    AssistantTurnCompleted = 1,
    ToolResultRecorded = 2,
    RunInterrupted = 3,
    RunStopped = 4,
    RunFailed = 5,
}

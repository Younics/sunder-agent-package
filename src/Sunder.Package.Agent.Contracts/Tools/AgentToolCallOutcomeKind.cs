namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentToolCallOutcomeKind
{
    Executed = 0,
    WaitingForApproval = 1,
    Denied = 2,
    Failed = 3,
}

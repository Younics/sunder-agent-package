namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPromptContextRequest(
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    AgentTurnContextRecord Turn,
    IReadOnlyList<AgentTurnRecord> Turns,
    IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns,
    AgentPromptContextPlan ContextPlan);

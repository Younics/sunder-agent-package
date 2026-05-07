namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentMemoryRecallRequest(
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    AgentTurnContextRecord Turn,
    IReadOnlyList<AgentTurnRecord> Turns,
    IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns,
    AgentMemoryRecallPlan RecallPlan);

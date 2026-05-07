namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentLifecycleEvent(
    AgentLifecycleEventKind Kind,
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    AgentTurnContextRecord Turn,
    IReadOnlyList<AgentTurnRecord> Turns,
    IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns,
    AgentTurnRecord? TriggerTurn = null,
    AgentRunCheckpointRecord? Checkpoint = null);

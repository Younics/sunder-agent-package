namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Supplies immutable run and transcript snapshots to a durable-memory retrieval implementation.
/// </summary>
/// <remarks>
/// The request does not clone either turn collection. They are ordered transcript snapshots selected by the host,
/// not live views, and implementations must not mutate them or objects reachable through them. A provider should
/// observe its method cancellation token before expensive persistence or embedding work.
/// </remarks>
/// <param name="Session">The session snapshot that scopes memory ownership.</param>
/// <param name="Run">The run-generation snapshot for correlation and stale-work detection.</param>
/// <param name="Turn">The active user-turn context.</param>
/// <param name="Turns">The bounded transcript selection available for recall planning, in transcript order.</param>
/// <param name="RecentLiveBufferTurns">The smaller recent-turn selection intended for continuity signals, in transcript order.</param>
/// <param name="RecallPlan">The intent, query, category hints, and output bounds for this operation.</param>
public sealed record AgentMemoryRecallRequest(
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    AgentTurnContextRecord Turn,
    IReadOnlyList<AgentTurnRecord> Turns,
    IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns,
    AgentMemoryRecallPlan RecallPlan);

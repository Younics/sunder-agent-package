namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Contains the bounded transcript projection selected for one provider request.</summary>
public sealed record AgentSessionPromptProjection(
    IReadOnlyList<AgentTurnRecord> PromptTurns,
    bool SummaryUpdated,
    int OmittedHistoricalTurnCount,
    AgentSessionContextCheckpointRecord? ContextCheckpoint = null);

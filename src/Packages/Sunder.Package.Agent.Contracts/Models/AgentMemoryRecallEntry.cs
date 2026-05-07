namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentMemoryRecallEntry(
    string MemoryId,
    string Category,
    string Content,
    string? EvidenceText,
    float Score,
    bool IsPinned,
    AgentMemoryTrustState TrustState = AgentMemoryTrustState.Active,
    Guid? SourceTurnId = null,
    IReadOnlyList<AgentMemoryMatchReason>? MatchReasons = null);

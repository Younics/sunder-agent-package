namespace Sunder.Package.Agent.Memory.Semantic.Services;

public enum SemanticMemoryEntryIndexState
{
    Missing = 0,
    Stale = 1,
    Indexed = 2,
}

public sealed record SemanticMemoryMetricsSnapshot(
    long PromotionCandidateCount,
    long PromotionWriteCount,
    long RecallRequestCount,
    long RecallEntryCount,
    long CorrectionCount,
    long WorkerFailureCount);

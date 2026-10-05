namespace Sunder.Package.Agent.Memory.Semantic.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum SemanticMemoryEntryIndexState
{
    Missing = 0,
    Stale = 1,
    Indexed = 2,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record SemanticMemoryMetricsSnapshot(
    long PromotionCandidateCount,
    long PromotionWriteCount,
    long RecallRequestCount,
    long RecallEntryCount,
    long CorrectionCount,
    long WorkerFailureCount);

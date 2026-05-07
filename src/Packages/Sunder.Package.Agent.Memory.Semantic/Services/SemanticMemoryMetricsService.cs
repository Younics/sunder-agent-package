using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticMemoryMetricsService
{
    private long _promotionCandidateCount;
    private long _promotionWriteCount;
    private long _recallRequestCount;
    private long _recallEntryCount;
    private long _correctionCount;
    private long _workerFailureCount;

    public void RecordPromotion(int candidateCount, int committedCount)
    {
        Interlocked.Add(ref _promotionCandidateCount, candidateCount);
        Interlocked.Add(ref _promotionWriteCount, committedCount);
    }

    public void RecordRecall(AgentMemoryRecallPlan recallPlan, int returnedEntryCount)
    {
        if (recallPlan.ShouldRecall)
        {
            Interlocked.Increment(ref _recallRequestCount);
        }

        Interlocked.Add(ref _recallEntryCount, returnedEntryCount);
    }

    public void RecordCorrection()
        => Interlocked.Increment(ref _correctionCount);

    public void RecordWorkerFailure()
        => Interlocked.Increment(ref _workerFailureCount);

    public SemanticMemoryMetricsSnapshot GetSnapshot()
        => new(
            PromotionCandidateCount: Interlocked.Read(ref _promotionCandidateCount),
            PromotionWriteCount: Interlocked.Read(ref _promotionWriteCount),
            RecallRequestCount: Interlocked.Read(ref _recallRequestCount),
            RecallEntryCount: Interlocked.Read(ref _recallEntryCount),
            CorrectionCount: Interlocked.Read(ref _correctionCount),
            WorkerFailureCount: Interlocked.Read(ref _workerFailureCount));
}

public sealed record SemanticMemoryMetricsSnapshot(
    long PromotionCandidateCount,
    long PromotionWriteCount,
    long RecallRequestCount,
    long RecallEntryCount,
    long CorrectionCount,
    long WorkerFailureCount);

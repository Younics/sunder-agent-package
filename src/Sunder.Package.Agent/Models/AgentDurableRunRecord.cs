using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal readonly record struct AgentDurableRunKey(
    Guid RunId,
    Guid SessionId,
    long RunRevision);

internal sealed record AgentDurableRunRecord(
    AgentDurableRunKey Key,
    long Epoch,
    AgentDurableRunStatus Status,
    string ProfileId,
    string UserMessage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    AgentRunSuspension? Suspension = null,
    string? ContinuationToken = null,
    long ProviderCycleCount = 0,
    long ToolCallCount = 0,
    long SubmittedContextTokenCount = 0)
{
    public AgentRunBudgetState BudgetState
        => new(ProviderCycleCount, ToolCallCount, SubmittedContextTokenCount);
}

internal readonly record struct AgentRunBudgetState(
    long ProviderCycles,
    long ToolCalls,
    long SubmittedContextTokens);

internal readonly record struct AgentRunBudgetCharge(
    long ProviderCycles = 0,
    long ToolCalls = 0,
    long SubmittedContextTokens = 0);

internal sealed class AgentDurableRunLease(AgentDurableRunRecord run)
{
    private readonly Queue<Action> _notificationQueue = [];

    public AgentDurableRunKey Key { get; } = run.Key;

    public long Epoch { get; private set; } = run.Epoch;

    internal object SyncRoot { get; } = new();

    internal bool IsDispatchingNotifications { get; set; }

    internal Queue<Action> NotificationQueue => _notificationQueue;

    internal void AdvanceTo(long epoch)
    {
        if (epoch <= Epoch)
        {
            throw new InvalidOperationException("A durable run lease can only advance to a newer epoch.");
        }

        Epoch = epoch;
    }
}

internal sealed record AgentRunTransitionResult(
    AgentDurableRunRecord Run,
    AgentRunCheckpointRecord Checkpoint)
{
    public IReadOnlyList<AgentCompletedStreamingTurn> CompletedStreamingTurns { get; init; } = [];
}

internal sealed record AgentRunStopPersistenceResult(
    AgentRunTransitionResult Transition,
    IReadOnlyList<AgentCompletedStreamingTurn> CompletedStreamingTurns);

internal readonly record struct AgentCompletedStreamingTurn(
    AgentTurnRecord Turn,
    int ContentLength);

internal sealed record AgentCheckpointPersistenceResult(
    AgentRunCheckpointRecord Checkpoint,
    IReadOnlyList<AgentCompletedStreamingTurn> CompletedStreamingTurns);

internal sealed record AgentRunStartPersistenceResult(
    AgentRunTransitionResult Transition,
    AgentTurnRecord UserTurn,
    AgentTranscriptRollbackResult? Rollback);

internal sealed class AgentRunStartCleanupException(IReadOnlyList<Exception> failures)
    : AggregateException(
        "Run started, but one or more external rollback cleanup steps failed.",
        failures);

internal sealed class AgentRunTranscriptWriteRejectedException()
    : InvalidOperationException("The durable run changed before its transcript mutation could be committed.");

internal enum AgentDurableRunStatus
{
    Preparing = 0,
    Idle = 1,
    Running = 2,
    Interrupted = 3,
    Stopped = 4,
    Completed = 5,
    Failed = 6,
    WaitingForApproval = 7,
}

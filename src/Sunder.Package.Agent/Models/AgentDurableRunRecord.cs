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
    string? ContinuationToken = null);

internal sealed class AgentDurableRunLease(AgentDurableRunRecord run)
{
    public AgentDurableRunKey Key { get; } = run.Key;

    public long Epoch { get; private set; } = run.Epoch;

    internal object SyncRoot { get; } = new();

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
    AgentRunCheckpointRecord Checkpoint);

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

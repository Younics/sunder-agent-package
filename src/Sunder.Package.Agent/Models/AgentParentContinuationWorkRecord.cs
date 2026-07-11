using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal sealed record AgentParentContinuationWorkRecord(
    string WorkId,
    AgentDurableRunKey ParentRunKey,
    Guid ChildSessionId,
    string ToolCallId,
    AgentRunStatus ChildStatus,
    string Summary,
    string? Content,
    string? Title,
    AgentChildJoinRunSuspension? Join,
    AgentParentContinuationWorkStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExecutionStartedAtUtc = null,
    string? LastError = null);

internal sealed record AgentParentContinuationDispatchResult(
    AgentParentContinuationWorkRecord Work,
    AgentDurableRunRecord Run,
    AgentChildJoinRunSuspension Join,
    AgentRunCheckpointRecord RunningCheckpoint);

internal enum AgentParentContinuationWorkStatus
{
    Pending = 0,
    Ready = 1,
    Dispatching = 2,
    Completed = 3,
    Failed = 4,
}

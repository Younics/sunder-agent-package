using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

public sealed record AgentPendingPermissionRequestRecord(
    string RequestId,
    Guid SessionId,
    Guid RunId,
    long RunRevision,
    string? ProfileId,
    Guid UserTurnId,
    string UserMessage,
    string CallId,
    string ActionId,
    string BoundaryId,
    string Summary,
    string? ToolId,
    string ArgumentsJson,
    string? Command,
    string? Path,
    string? WorkspaceId,
    string? BindingId,
    string? ResourceDisplayName,
    string? ResourceReference,
    bool IsMutation,
    DateTimeOffset CreatedAtUtc,
    Guid? ParentSessionId = null,
    Guid? RootSessionId = null,
    AgentPendingPermissionStatus Status = AgentPendingPermissionStatus.Pending,
    string? ClaimToken = null,
    DateTimeOffset? ClaimedAtUtc = null,
    DateTimeOffset? DecidedAtUtc = null,
    string? DecisionSummary = null,
    string ExecutionFingerprint = "",
    string? ContinuationToken = null,
    DateTimeOffset? ClaimLeaseExpiresAtUtc = null,
    DateTimeOffset? ContinuationConsumedAtUtc = null,
    DateTimeOffset? ExecutionStartedAtUtc = null);

public enum AgentPendingPermissionStatus
{
    Pending = 0,
    Claimed = 1,
    Executed = 2,
    Denied = 3,
    Failed = 4,
    Expired = 5,
}

internal sealed record AgentPendingPermissionClaimResult(
    AgentPendingPermissionClaimOutcome Outcome,
    AgentPendingPermissionRequestRecord? Request = null)
{
    public bool IsClaimed => Outcome == AgentPendingPermissionClaimOutcome.Claimed;
}

internal enum AgentPendingPermissionClaimOutcome
{
    Claimed = 0,
    NotFound = 1,
    AlreadyClaimed = 2,
    AlreadyDecided = 3,
    InvalidSuspension = 4,
}

internal sealed record AgentPendingPermissionDecisionResult(
    AgentPendingPermissionDecisionOutcome Outcome,
    AgentPendingPermissionRequestRecord? Request = null,
    AgentRunCheckpointRecord? Checkpoint = null)
{
    public bool IsDecided => Outcome == AgentPendingPermissionDecisionOutcome.Decided;
}

internal enum AgentPendingPermissionDecisionOutcome
{
    Decided = 0,
    NotFound = 1,
    AlreadyClaimed = 2,
    AlreadyDecided = 3,
}

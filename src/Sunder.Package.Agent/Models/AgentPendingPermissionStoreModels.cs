using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

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
    AgentCheckpointPersistenceResult? Finalization = null,
    AgentTurnRecord? ToolResultTurn = null)
{
    public bool IsDecided => Outcome == AgentPendingPermissionDecisionOutcome.Decided;

    public AgentRunCheckpointRecord? Checkpoint => Finalization?.Checkpoint;
}

internal sealed record AgentPermissionExpirationResult(
    bool Changed,
    AgentCheckpointPersistenceResult? Finalization = null);

internal enum AgentPendingPermissionDecisionOutcome
{
    Decided = 0,
    NotFound = 1,
    AlreadyClaimed = 2,
    AlreadyDecided = 3,
}

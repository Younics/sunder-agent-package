using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentPendingPermissionRequestRecord(
    string RequestId,
    Guid SessionId,
    Guid RunId,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long RunRevision,
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
    DateTimeOffset? ExecutionStartedAtUtc = null,
    string ExecutionSnapshotJson = "")
{
    /// <summary>Gets the durable tool execution awaiting this permission decision, when available.</summary>
    public Guid? ToolExecutionId { get; init; }

    /// <summary>Gets the schema version of the persisted durable resource-claim set.</summary>
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public int ResourceClaimSetVersion { get; init; }

    /// <summary>Gets every durable resource claim covered by this permission request.</summary>
    public IReadOnlyList<AgentResourceClaim> ResourceClaims { get; init; } = [];

    /// <summary>Gets the durable requested approval scope. This is intent, not an accepted permission grant.</summary>
    public bool ApproveForSession { get; init; }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentPendingPermissionStatus
{
    Pending = 0,
    Claimed = 1,
    Executed = 2,
    Denied = 3,
    Failed = 4,
    Expired = 5,
}

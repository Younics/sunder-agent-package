using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentPendingPermissionRequestRecord
{
    public required string ActionId { get; init; }
    public required JsonText ArgumentsJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BindingId { get; init; }
    public required string BoundaryId { get; init; }
    public required string CallId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> ClaimLeaseExpiresAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ClaimToken { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> ClaimedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Command { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> ContinuationConsumedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ContinuationToken { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> DecidedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> DecisionSummary { get; init; }
    public required string ExecutionFingerprint { get; init; }
    public required JsonText ExecutionSnapshotJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> ExecutionStartedAtUtc { get; init; }
    public required bool IsMutation { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ParentSessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Path { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    public required string RequestId { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long ResourceClaimSetVersion { get; init; }
    public required IReadOnlyList<AgentResourceClaim> ResourceClaims { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ResourceDisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ResourceReference { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RootSessionId { get; init; }
    public required Uuid RunId { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long RunRevision { get; init; }
    public required Uuid SessionId { get; init; }
    public required AgentPendingPermissionStatus Status { get; init; }
    public required string Summary { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ToolExecutionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolId { get; init; }
    public required string UserMessage { get; init; }
    public required Uuid UserTurnId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class AgentResourceClaim
{
    public required string ActionId { get; init; }
    public required string BindingGeneration { get; init; }
    public required string BindingId { get; init; }
    public required bool CaseSensitivePath { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ConfiguredRoot { get; init; }
    public required string ExecutionTargetOwnerPackageId { get; init; }
    public required string LogicalPath { get; init; }
    public required string NamespaceId { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long ResourceIndex { get; init; }
    public required string RootIdentityChainFingerprint { get; init; }
    public required bool TargetExists { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> TargetIdentity { get; init; }
    public required bool TargetIsAuthorityRoot { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> TargetKind { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolOwnerPackageId { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Version { get; init; }
    public required string WorkspaceGeneration { get; init; }
    public required string WorkspaceId { get; init; }
}

internal sealed class AgentSessionPermissionState
{
    public required bool IsUnrestrictedModeEnabled { get; init; }
    public required Uuid SessionId { get; init; }
}

internal sealed class AgentChatPermissionProjection
{
    public required IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentSessionPermissionState> SessionState { get; init; }
}

internal sealed class AgentPermissionProjection
{
    public required IReadOnlyList<AgentPermissionActionDescriptor> Actions { get; init; }
    public required IReadOnlyList<AgentPermissionOverride> Overrides { get; init; }
    public required IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentSessionPermissionState> SessionState { get; init; }
}

internal sealed class AgentPermissionActionDescriptor
{
    public required string ActionId { get; init; }
    public required IReadOnlyList<AgentPermissionBoundaryDescriptor> Boundaries { get; init; }
    public required string Description { get; init; }
    public required string DisplayName { get; init; }
}

internal sealed class AgentPermissionBoundaryDescriptor
{
    public required string BoundaryId { get; init; }
    public required AgentPermissionDecision DefaultDecision { get; init; }
    public required string Description { get; init; }
    public required string DisplayName { get; init; }
}

internal sealed class AgentPermissionOverride
{
    public required string ActionId { get; init; }
    public required string BoundaryId { get; init; }
    public required AgentPermissionDecision Decision { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentProfileCommand
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>> Assignments { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BehaviorLoopId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> BehaviorLoopSettingsJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BehaviorLoopSourceId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ChatModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> ChatModelSettingsJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ChatProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Instructions { get; init; }
    public required AgentProfileCommandKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
}

internal sealed class AgentProfileCommandResult
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentProfileRecord> Profile { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
}

internal sealed class AgentWorkspaceCommand
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentWorkspaceDocumentRecord>> Documents { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ExecutionTargetId { get; init; }
    public required AgentWorkspaceCommandKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentWorkspacePathRecord>> Paths { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class AgentWorkspaceCommandResult
{
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentExecutionTargetWarmupResult> Warmup { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
}

internal sealed class AgentSessionCommand
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BehaviorLoopId { get; init; }
    public required AgentSessionCommandKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentSessionRecord> Session { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Title { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class AgentSessionCommandResult
{
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentSessionSnapshot> Session { get; init; }
}

internal sealed class AgentRunCommand
{
    public required bool ApproveForSession { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentAttachmentUploadHandle>> AttachmentHandles { get; init; }
    public required AgentRunCommandKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> PermissionRequestId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RollbackAnchorTurnId { get; init; }
    public required Uuid SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> UserMessage { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> UserTurnId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class AgentRunCommandResult
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentRunCheckpointRecord> Checkpoint { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RunId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> UserTurnId { get; init; }
}

internal sealed class AgentRunCommandStatusRequest
{
    public required Uuid SessionId { get; init; }
    public required Uuid UserTurnId { get; init; }
}

internal sealed class AgentRunCommandStatusResult
{
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    public required AgentRunCommandStatus Status { get; init; }
}

internal sealed class AgentPermissionCommand
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ActionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BoundaryId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentPermissionDecision> Decision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<bool> IsEnabled { get; init; }
    public required AgentPermissionCommandKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> SessionId { get; init; }
}

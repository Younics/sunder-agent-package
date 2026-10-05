using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentTransportProfileRequest
{
    public required long RequestValueBytes { get; init; }
    public required long ResponseValueBytes { get; init; }
    public required long EventValueBytes { get; init; }
    public required long ContentReferenceBytes { get; init; }
    public required bool RuntimeReported { get; init; }
}

internal sealed class AgentTransportProfile
{
    public required long RequestValueBytes { get; init; }
    public required long ResponseValueBytes { get; init; }
    public required long EventValueBytes { get; init; }
    public required long ContentReferenceBytes { get; init; }
    public required bool RuntimeReported { get; init; }
}

internal sealed class AgentChatSnapshotRequest
{
    public required bool IncludeInitialTranscript { get; init; }
    [SunderRpcFacet(Minimum = "1", Maximum = "500")] public required long InitialTranscriptLimit { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> PreferredProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> PreferredSessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> PreferredWorkspaceId { get; init; }
}

internal sealed class AgentChatSnapshotProjection
{
    public required AgentTranscriptPage InitialTranscript { get; init; }
    public required AgentChatPermissionProjection Permissions { get; init; }
    public required IReadOnlyList<AgentProfileRecord> Profiles { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> RuntimeInstanceId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentProfileRecord> SelectedProfile { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentSessionSnapshot> SelectedSession { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentWorkspaceRecord> SelectedWorkspace { get; init; }
    public required IReadOnlyList<AgentWorkspaceBindingRecord> WorkspaceBindings { get; init; }
    public required IReadOnlyList<AgentSessionSnapshot> WorkspaceSessions { get; init; }
    public required IReadOnlyList<AgentWorkspaceRecord> Workspaces { get; init; }
}

internal sealed record AgentDashboardRequest;

internal sealed class AgentDashboardProjection
{
    public required IReadOnlyList<AgentProfileRecord> Profiles { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    public required IReadOnlyList<AgentWorkspaceBindingRecord> WorkspaceBindings { get; init; }
    public required IReadOnlyList<AgentWorkspaceRecord> Workspaces { get; init; }
}

internal sealed class AgentChangeSubscription
{
    [SunderRpcFacet(Minimum = "0")] public required long AfterRevision { get; init; }
    public required bool SupportsTurnMutations { get; init; }
}

internal sealed class AgentRuntimeChange
{
    public required AgentRuntimeChangeKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentProfileRecord> Profile { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentRunActivityUpdate> RunActivity { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> RuntimeInstanceId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentSessionSnapshot> Session { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentTurnRecord> Turn { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentTurnMutation> TurnMutation { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class AgentRunActivityUpdate
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required AgentRunActivityKind Kind { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long RunRevision { get; init; }
    public required string Text { get; init; }
}

internal sealed class AgentTurnMutation
{
    [SunderRpcFacet(Minimum = "0")] public required long BaseContentLength { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long ContentRevision { get; init; }
    public required AgentTurnMutationKind Kind { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long RuntimeRevision { get; init; }
    public required Uuid SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Text { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentTurnRecord> Turn { get; init; }
    public required Uuid TurnId { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

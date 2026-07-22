using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Runtime;

internal static class AgentRuntimeOperations
{
    public static readonly PackageRuntimeOperation<AgentChatSnapshotRequest, AgentChatSnapshotProjection> ChatSnapshot =
        new("agent.chat.snapshot.v1");
    public static readonly PackageRuntimeOperation<AgentDashboardRequest, AgentDashboardProjection> Dashboard =
        new("agent.dashboard.v1");
    public static readonly PackageRuntimeOperation<AgentTranscriptPageRequest, AgentTranscriptPage> Transcript =
        new("agent.transcript.page.v1");
    public static readonly PackageRuntimeOperation<AgentCatalogRequest, AgentCatalogProjection> Catalog =
        new("agent.catalog.v1");
    public static readonly PackageRuntimeOperation<AgentProfileCommand, AgentProfileCommandResult> Profiles =
        new("agent.profiles.command.v1");
    public static readonly PackageRuntimeOperation<AgentWorkspaceCommand, AgentWorkspaceCommandResult> Workspaces =
        new("agent.workspaces.command.v1");
    public static readonly PackageRuntimeOperation<AgentSessionCommand, AgentSessionCommandResult> SessionCommands =
        new("agent.sessions.command.v1");
    public static readonly PackageRuntimeOperation<AgentRunCommand, AgentRunCommandResult> Runs =
        new("agent.runs.command.v1");
    public static readonly PackageRuntimeOperation<AgentRunCommandStatusRequest, AgentRunCommandStatusResult> RunStatus =
        new("agent.runs.status.v1");
    public static readonly PackageRuntimeOperation<AgentPermissionCommand, AgentPermissionProjection> Permissions =
        new("agent.permissions.command.v1");
    public static readonly PackageRuntimeOperation<AgentAttachmentTransferRequest, AgentAttachmentTransferResult> AttachmentTransfers =
        new("agent.attachments.transfer.v1");
    public static readonly PackageRuntimeStream<AgentChangeSubscription, AgentRuntimeChange> Changes =
        new("agent.changes.v1");
}

internal sealed record AgentChatSnapshotRequest(
    int InitialTranscriptLimit = 60,
    string? PreferredProfileId = null,
    string? PreferredWorkspaceId = null,
    Guid? PreferredSessionId = null);
internal sealed record AgentChatPermissionProjection(
    long Revision,
    AgentSessionPermissionState? SessionState,
    IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests);
internal sealed record AgentChatSnapshotProjection(
    long Revision,
    IReadOnlyList<AgentProfileRecord> Profiles,
    IReadOnlyList<AgentWorkspaceRecord> Workspaces,
    IReadOnlyList<AgentWorkspaceBindingRecord> WorkspaceBindings,
    AgentProfileRecord? SelectedProfile,
    AgentWorkspaceRecord? SelectedWorkspace,
    AgentSessionSnapshot? SelectedSession,
    IReadOnlyList<AgentSessionSnapshot> WorkspaceSessions,
    AgentTranscriptPage InitialTranscript,
    AgentChatPermissionProjection Permissions,
    string? RuntimeInstanceId = null);

internal interface IAgentChatSnapshotGateway
{
    event Action<AgentChatSnapshotProjection>? ChatSnapshotReloaded;

    Task<AgentChatSnapshotProjection> LoadChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken = default);

    void CompleteChatSnapshot(AgentChatSnapshotProjection snapshot, bool applied);
}

internal sealed record AgentDashboardRequest;
internal sealed record AgentDashboardProjection(
    long Revision,
    IReadOnlyList<AgentProfileRecord> Profiles,
    IReadOnlyList<AgentWorkspaceRecord> Workspaces,
    IReadOnlyList<AgentWorkspaceBindingRecord> WorkspaceBindings);

internal sealed record AgentSessionSnapshot(AgentSessionRecord Session, AgentRunCheckpointRecord? Checkpoint);

internal enum AgentTranscriptPageDirection { Recent, Before, After, Turn }
internal sealed record AgentTranscriptPageRequest(
    Guid SessionId,
    AgentTranscriptPageDirection Direction,
    int Limit = 60,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null);
internal sealed record AgentTranscriptPage(long Revision, IReadOnlyList<AgentTurnRecord> Turns, bool HasMore);

internal interface IAgentTranscriptPageGateway
{
    Task<AgentTranscriptPage> LoadTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken = default);
}

internal interface IAgentChatSessionCommandGateway
{
    Task<AgentSessionSnapshot> CreateRootSessionAsync(
        string title,
        string profileId,
        string? behaviorLoopId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<AgentSessionSnapshot> UpdateSessionAsync(
        AgentSessionRecord session,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

internal interface IAgentChatPermissionCommandGateway
{
    Task<AgentChatPermissionProjection> LoadSessionPermissionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<AgentChatPermissionProjection> SetSessionUnrestrictedModeAsync(
        Guid sessionId,
        bool isEnabled,
        CancellationToken cancellationToken = default);
}

internal interface IAgentChatRunGateway
{
    Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
        Guid sessionId,
        string requestId,
        bool approveForSession,
        CancellationToken cancellationToken = default);
}

internal interface IAgentCorrelatedRunGateway
{
    Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid userTurnId,
        CancellationToken cancellationToken = default);

    Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
        Guid sessionId,
        Guid rollbackAnchorTurnId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid userTurnId,
        CancellationToken cancellationToken = default);
}

internal interface IAgentRunCommandStatusGateway
{
    Task<AgentRunCommandStatus> GetRunCommandStatusAsync(
        Guid sessionId,
        Guid userTurnId,
        CancellationToken cancellationToken = default);
}

internal sealed record AgentCatalogRequest(
    AgentProfileRecord? Profile = null,
    string? ChatProviderId = null,
    string? EmbeddingProviderId = null);
internal sealed record AgentCatalogProjection(
    long Revision,
    IReadOnlyList<AgentProviderDescriptor> ChatProviders,
    IReadOnlyList<AgentEmbeddingProviderDescriptor> EmbeddingProviders,
    IReadOnlyList<AgentBehaviorLoopDescriptor> BehaviorLoops,
    IReadOnlyList<AgentExecutionTargetDescriptor> ExecutionTargets,
    IReadOnlyList<AgentToolCatalogEntry> LocalTools,
    IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> SelectableCapabilities,
    bool HasEmbeddingConsumers,
    IReadOnlyList<AgentModelDescriptor> ChatModels,
    IReadOnlyList<AgentEmbeddingModelDescriptor> EmbeddingModels,
    AgentProviderReadiness? ChatReadiness,
    AgentEmbeddingProviderReadiness? EmbeddingReadiness);

internal enum AgentProfileCommandKind { Create, Save, Delete }
internal sealed record AgentProfileCommand(
    AgentProfileCommandKind Kind,
    string? ProfileId = null,
    string? DisplayName = null,
    string? Description = null,
    string? Instructions = null,
    string? ChatProviderId = null,
    string? ChatModelId = null,
    string? EmbeddingProviderId = null,
    string? EmbeddingModelId = null,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? Assignments = null,
    string? BehaviorLoopId = null,
    string? BehaviorLoopSourceId = null,
    string? BehaviorLoopSettingsJson = null,
    string? ChatModelSettingsJson = null);
internal sealed record AgentProfileCommandResult(long Revision, AgentProfileRecord? Profile);

internal enum AgentWorkspaceCommandKind { Create, Save, Delete, Warmup }
internal sealed record AgentWorkspaceCommand(
    AgentWorkspaceCommandKind Kind,
    string? WorkspaceId = null,
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyList<AgentWorkspacePathRecord>? Paths = null,
    IReadOnlyList<AgentWorkspaceDocumentRecord>? Documents = null,
    string? ExecutionTargetId = null);
internal sealed record AgentWorkspaceCommandResult(
    long Revision,
    AgentWorkspaceRecord? Workspace,
    AgentExecutionTargetWarmupResult? Warmup = null);

internal enum AgentSessionCommandKind { Create, Update, Delete }
internal sealed record AgentSessionCommand(
    AgentSessionCommandKind Kind,
    AgentSessionRecord? Session = null,
    string? Title = null,
    string? ProfileId = null,
    string? BehaviorLoopId = null,
    string? WorkspaceId = null);
internal sealed record AgentSessionCommandResult(long Revision, AgentSessionSnapshot? Session);

internal enum AgentRunCommandKind { Start, RollbackAndStart, Stop, ApprovePermission, DenyPermission }
internal sealed record AgentRunCommand(
    AgentRunCommandKind Kind,
    Guid SessionId,
    string? ProfileId = null,
    string? UserMessage = null,
    string? WorkspaceId = null,
    IReadOnlyList<AgentAttachmentUploadHandle>? AttachmentHandles = null,
    Guid? RollbackAnchorTurnId = null,
    string? PermissionRequestId = null,
    bool ApproveForSession = false,
    Guid? UserTurnId = null);
internal sealed record AgentRunCommandResult(long Revision, AgentRunCheckpointRecord? Checkpoint);
internal sealed record AgentRunCommandStatusRequest(Guid SessionId, Guid UserTurnId);
internal enum AgentRunCommandStatus { Pending, Committed, Absent }
internal sealed record AgentRunCommandStatusResult(long Revision, AgentRunCommandStatus Status);

internal enum AgentPermissionCommandKind { Read, SetUnrestricted, SaveOverride, DeleteOverride, SaveSessionApproval }
internal sealed record AgentPermissionCommand(
    AgentPermissionCommandKind Kind,
    Guid? SessionId = null,
    bool? IsEnabled = null,
    string? ActionId = null,
    string? BoundaryId = null,
    AgentPermissionDecision? Decision = null);
internal sealed record AgentPermissionProjection(
    long Revision,
    AgentSessionPermissionState? SessionState,
    IReadOnlyList<AgentPermissionActionDescriptor> Actions,
    IReadOnlyList<AgentPermissionOverride> Overrides,
    IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests);

internal enum AgentAttachmentTransferKind
{
    BeginUpload,
    WriteUploadChunk,
    CompleteUpload,
    AbortUpload,
    ReadDownloadChunk,
}

internal sealed record AgentAttachmentUploadDescriptor(
    string FileName,
    string? MediaType,
    int SizeBytes,
    string Sha256);
internal sealed record AgentAttachmentUploadHandle(string TransferId);
internal sealed record AgentAttachmentTransferRequest(
    AgentAttachmentTransferKind Kind,
    string? TransferId = null,
    AgentAttachmentUploadDescriptor? Upload = null,
    int Offset = 0,
    byte[]? Content = null,
    AgentAttachmentMetadata? Metadata = null);
internal sealed record AgentAttachmentTransferResult(
    string? TransferId = null,
    int NextOffset = 0,
    int TotalBytes = 0,
    byte[]? Content = null,
    bool IsComplete = false);

internal sealed record AgentChangeSubscription(
    long AfterRevision = 0,
    bool SupportsTurnMutations = false);
internal enum AgentRuntimeChangeKind
{
    Connected = 0,
    ResnapshotRequired = 1,
    Profile = 2,
    Catalog = 3,
    Workspace = 4,
    Session = 5,
    Turn = 6,
    TranscriptReset = 7,
    RunActivity = 8,
    Permission = 9,
    TurnMutation = 10,
}
internal sealed record AgentRuntimeChange(
    long Revision,
    AgentRuntimeChangeKind Kind,
    string? ProfileId = null,
    string? WorkspaceId = null,
    Guid? SessionId = null,
    AgentProfileRecord? Profile = null,
    AgentWorkspaceRecord? Workspace = null,
    AgentSessionSnapshot? Session = null,
    AgentTurnRecord? Turn = null,
    AgentTurnMutation? TurnMutation = null,
    AgentRunActivityUpdate? RunActivity = null,
    string? RuntimeInstanceId = null);

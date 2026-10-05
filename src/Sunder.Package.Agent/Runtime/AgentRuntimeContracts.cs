using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTransportProfileRequest(
    int RequestValueBytes,
    int ResponseValueBytes,
    int EventValueBytes,
    int ContentReferenceBytes,
    bool RuntimeReported);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTransportProfile(
    int RequestValueBytes,
    int ResponseValueBytes,
    int EventValueBytes,
    int ContentReferenceBytes,
    bool RuntimeReported)
{
    internal static AgentTransportProfile StandardFallback { get; } = new(
        8 * 1024 * 1024,
        8 * 1024 * 1024,
        8 * 1024 * 1024,
        64 * 1024 * 1024,
        RuntimeReported: false);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentChatSnapshotRequest(
    int InitialTranscriptLimit = 60,
    string? PreferredProfileId = null,
    string? PreferredWorkspaceId = null,
    Guid? PreferredSessionId = null,
    bool IncludeInitialTranscript = true);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentChatPermissionProjection(
    long Revision,
    AgentSessionPermissionState? SessionState,
    IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentDashboardRequest;
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentDashboardProjection(
    long Revision,
    IReadOnlyList<AgentProfileRecord> Profiles,
    IReadOnlyList<AgentWorkspaceRecord> Workspaces,
    IReadOnlyList<AgentWorkspaceBindingRecord> WorkspaceBindings);

internal interface IAgentDashboardLoader
{
    event Action? DashboardInvalidated;

    Task<AgentDashboardProjection> LoadDashboardAsync(
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentSessionSnapshot(AgentSessionRecord Session, AgentRunCheckpointRecord? Checkpoint);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentTranscriptPageDirection { Recent, Before, After, Turn }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTranscriptPageRequest(
    Guid SessionId,
    AgentTranscriptPageDirection Direction,
    int Limit = 60,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTranscriptPage(
    long Revision,
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasMore,
    TranscriptPageCursor? Continuation = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTranscriptToolDetailResult(
    bool Found,
    AgentTranscriptToolDetailRecord? Detail);

internal interface IAgentTranscriptPageGateway
{
    Task<AgentTranscriptPage> LoadTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Loads exact tool detail for lightweight transcript rows.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public interface IAgentTranscriptToolDetailGateway
{
    /// <summary>Loads the detail matching the request's exact session and tool identity.</summary>
    Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface IAgentChatRunGateway
{
    Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
        Guid sessionId,
        string requestId,
        bool approveForSession,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface IAgentRunCommandStatusGateway
{
    Task<AgentRunCommandStatus> GetRunCommandStatusAsync(
        Guid sessionId,
        Guid userTurnId,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentCatalogRequest(
    AgentProfileRecord? Profile = null,
    string? ChatProviderId = null,
    string? EmbeddingProviderId = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

internal interface IAgentCatalogLoader
{
    Task<AgentCatalogProjection> LoadCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentProfileCommandKind { Create, Save, Delete }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentProfileCommandResult(long Revision, AgentProfileRecord? Profile);

internal interface IAgentProfileCommandGateway
{
    Task SaveProfileAsync(
        string profileId,
        string displayName,
        string? description,
        string? instructions,
        string? chatProviderId,
        string? chatModelId,
        string? embeddingProviderId,
        string? embeddingModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
        string? behaviorLoopId = null,
        string? behaviorLoopSourceId = null,
        string? behaviorLoopSettingsJson = null,
        string? chatModelSettingsJson = null,
        CancellationToken cancellationToken = default);

    Task DeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentWorkspaceCommandKind { Create, Save, Delete, Warmup }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentWorkspaceCommand(
    AgentWorkspaceCommandKind Kind,
    string? WorkspaceId = null,
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyList<AgentWorkspacePathRecord>? Paths = null,
    IReadOnlyList<AgentWorkspaceDocumentRecord>? Documents = null,
    string? ExecutionTargetId = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentWorkspaceCommandResult(
    long Revision,
    AgentWorkspaceRecord? Workspace,
    AgentExecutionTargetWarmupResult? Warmup = null);

internal interface IAgentWorkspaceCommandGateway
{
    Task<AgentWorkspaceRecord> CreateWorkspaceAsync(
        string displayName,
        CancellationToken cancellationToken = default);

    Task SaveWorkspaceAggregateAsync(
        string workspaceId,
        string displayName,
        string? description,
        IReadOnlyList<AgentWorkspacePathRecord> paths,
        IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
        string? executionTargetId,
        CancellationToken cancellationToken = default);

    Task DeleteWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentSessionCommandKind { Create, Update, Delete }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentSessionCommand(
    AgentSessionCommandKind Kind,
    AgentSessionRecord? Session = null,
    string? Title = null,
    string? ProfileId = null,
    string? BehaviorLoopId = null,
    string? WorkspaceId = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentSessionCommandResult(long Revision, AgentSessionSnapshot? Session);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentRunCommandKind { Start, RollbackAndStart, Stop, ApprovePermission, DenyPermission }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentRunCommandResult(
    long Revision,
    AgentRunCheckpointRecord? Checkpoint,
    Guid? RunId = null,
    Guid? UserTurnId = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentRunCommandStatusRequest(Guid SessionId, Guid UserTurnId);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentRunCommandStatus { Pending, Committed, Absent }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentRunCommandStatusResult(long Revision, AgentRunCommandStatus Status);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum AgentPermissionCommandKind { Read, SetUnrestricted, SaveOverride, DeleteOverride }
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentPermissionCommand(
    AgentPermissionCommandKind Kind,
    Guid? SessionId = null,
    bool? IsEnabled = null,
    string? ActionId = null,
    string? BoundaryId = null,
    AgentPermissionDecision? Decision = null);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentPermissionProjection(
    long Revision,
    AgentSessionPermissionState? SessionState,
    IReadOnlyList<AgentPermissionActionDescriptor> Actions,
    IReadOnlyList<AgentPermissionOverride> Overrides,
    IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests);

internal interface IAgentGlobalPermissionGateway
{
    event Action? GlobalPermissionsInvalidated;

    Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
        CancellationToken cancellationToken = default);

    Task SaveOverrideAsync(
        string actionId,
        string boundaryId,
        AgentPermissionDecision decision,
        CancellationToken cancellationToken = default);

    Task DeleteOverrideAsync(
        string actionId,
        string boundaryId,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentUploadDescriptor(
    string FileName,
    string? MediaType,
    int SizeBytes,
    string Sha256);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentUploadHandle(string TransferId);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentContentUploadRequest(
    AgentAttachmentUploadDescriptor Upload,
    SunderRpcContentReference Content);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentContentUploadResult(AgentAttachmentUploadHandle Handle);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentContentDownloadRequest(AgentAttachmentMetadata Metadata);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentContentDownloadResult(SunderRpcContentReference Content);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentContentDiscardRequest(AgentAttachmentUploadHandle Handle);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentAttachmentContentDiscardResult(bool Discarded);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentChangeSubscription(
    long AfterRevision = 0,
    bool SupportsTurnMutations = false);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

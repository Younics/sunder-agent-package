using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.OwnerProtocol.Generated;

namespace Sunder.Package.Agent.Runtime;

internal static class AgentRuntimeOperations
{
    public static readonly AgentOwnerRpcOperation<AgentTransportProfileRequest, AgentTransportProfile> TransportProfile =
        AgentOwnerRuntimeRpc.CreateOperation<AgentTransportProfileRequest, AgentTransportProfile, Wire.AgentTransportProfileRequest, Wire.AgentTransportProfile>(
            static (client, request, token) => client.TransportProfileAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentChatSnapshotRequest, AgentChatSnapshotProjection> ChatSnapshot =
        AgentOwnerRuntimeRpc.CreateOperation<AgentChatSnapshotRequest, AgentChatSnapshotProjection, Wire.AgentChatSnapshotRequest, Wire.AgentChatSnapshotProjection>(
            static (client, request, token) => client.ChatSnapshotAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentDashboardRequest, AgentDashboardProjection> Dashboard =
        AgentOwnerRuntimeRpc.CreateOperation<AgentDashboardRequest, AgentDashboardProjection, Wire.AgentDashboardRequest, Wire.AgentDashboardProjection>(
            static (client, request, token) => client.DashboardAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentTranscriptPageRequest, AgentTranscriptPage> Transcript =
        AgentOwnerRuntimeRpc.CreateOperation<AgentTranscriptPageRequest, AgentTranscriptPage, Wire.AgentTranscriptPageRequest, Wire.AgentTranscriptPage>(
            static (client, request, token) => client.TranscriptPageAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentCatalogRequest, AgentCatalogProjection> Catalog =
        AgentOwnerRuntimeRpc.CreateOperation<AgentCatalogRequest, AgentCatalogProjection, Wire.AgentCatalogRequest, Wire.AgentCatalogProjection>(
            static (client, request, token) => client.CatalogAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentProfileCommand, AgentProfileCommandResult> Profiles =
        AgentOwnerRuntimeRpc.CreateOperation<AgentProfileCommand, AgentProfileCommandResult, Wire.AgentProfileCommand, Wire.AgentProfileCommandResult>(
            static (client, request, token) => client.ProfileCommandAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentWorkspaceCommand, AgentWorkspaceCommandResult> Workspaces =
        AgentOwnerRuntimeRpc.CreateOperation<AgentWorkspaceCommand, AgentWorkspaceCommandResult, Wire.AgentWorkspaceCommand, Wire.AgentWorkspaceCommandResult>(
            static (client, request, token) => client.WorkspaceCommandAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentSessionCommand, AgentSessionCommandResult> SessionCommands =
        AgentOwnerRuntimeRpc.CreateOperation<AgentSessionCommand, AgentSessionCommandResult, Wire.AgentSessionCommand, Wire.AgentSessionCommandResult>(
            static (client, request, token) => client.SessionCommandAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentRunCommand, AgentRunCommandResult> Runs =
        AgentOwnerRuntimeRpc.CreateOperation<AgentRunCommand, AgentRunCommandResult, Wire.AgentRunCommand, Wire.AgentRunCommandResult>(
            static (client, request, token) => client.RunCommandAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentRunCommandStatusRequest, AgentRunCommandStatusResult> RunStatus =
        AgentOwnerRuntimeRpc.CreateOperation<AgentRunCommandStatusRequest, AgentRunCommandStatusResult, Wire.AgentRunCommandStatusRequest, Wire.AgentRunCommandStatusResult>(
            static (client, request, token) => client.RunStatusAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentPermissionCommand, AgentPermissionProjection> Permissions =
        AgentOwnerRuntimeRpc.CreateOperation<AgentPermissionCommand, AgentPermissionProjection, Wire.AgentPermissionCommand, Wire.AgentPermissionProjection>(
            static (client, request, token) => client.PermissionCommandAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentAttachmentContentUploadRequest, AgentAttachmentContentUploadResult> AttachmentUploads =
        AgentOwnerRuntimeRpc.CreateOperation<AgentAttachmentContentUploadRequest, AgentAttachmentContentUploadResult, Wire.AgentAttachmentContentUploadRequest, Wire.AgentAttachmentContentUploadResult>(
            static (client, request, token) => client.AttachmentUploadAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentAttachmentContentDownloadRequest, AgentAttachmentContentDownloadResult> AttachmentDownloads =
        AgentOwnerRuntimeRpc.CreateOperation<AgentAttachmentContentDownloadRequest, AgentAttachmentContentDownloadResult, Wire.AgentAttachmentContentDownloadRequest, Wire.AgentAttachmentContentDownloadResult>(
            static (client, request, token) => client.AttachmentDownloadAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentAttachmentContentDiscardRequest, AgentAttachmentContentDiscardResult> AttachmentDiscards =
        AgentOwnerRuntimeRpc.CreateOperation<AgentAttachmentContentDiscardRequest, AgentAttachmentContentDiscardResult, Wire.AgentAttachmentContentDiscardRequest, Wire.AgentAttachmentContentDiscardResult>(
            static (client, request, token) => client.AttachmentDiscardAsync(request, token));
    public static readonly AgentOwnerRpcStream<AgentChangeSubscription, AgentRuntimeChange> Changes =
        AgentOwnerRuntimeRpc.CreateStream<AgentChangeSubscription, AgentRuntimeChange, Wire.AgentChangeSubscription, Wire.AgentRuntimeChange>(
            static (client, request, token) => client.ChangesAsync(request, token));
    public static readonly AgentOwnerRpcOperation<HistorySearchRequest, HistorySearchResponse> HistorySearch =
        AgentOwnerRuntimeRpc.CreateOperation<HistorySearchRequest, HistorySearchResponse, Wire.HistorySearchRequest, Wire.HistorySearchResponse>(
            static (client, request, token) => client.HistorySearchAsync(request, token));
    public static readonly AgentOwnerRpcOperation<HistorySearchStateRequest, HistorySearchState> HistoryState =
        AgentOwnerRuntimeRpc.CreateOperation<HistorySearchStateRequest, HistorySearchState, Wire.HistorySearchStateRequest, Wire.HistorySearchState>(
            static (client, request, token) => client.HistoryStateAsync(request, token));
    public static readonly AgentOwnerRpcOperation<HistorySearchCommand, HistorySearchCommandResult> HistoryCommands =
        AgentOwnerRuntimeRpc.CreateOperation<HistorySearchCommand, HistorySearchCommandResult, Wire.HistorySearchCommand, Wire.HistorySearchCommandResult>(
            static (client, request, token) => client.HistoryCommandAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentTranscriptAroundTurnRequest, AgentTranscriptAroundTurnPage> TranscriptAround =
        AgentOwnerRuntimeRpc.CreateOperation<AgentTranscriptAroundTurnRequest, AgentTranscriptAroundTurnPage, Wire.AgentTranscriptAroundTurnRequest, Wire.AgentTranscriptAroundTurnPage>(
            static (client, request, token) => client.TranscriptAroundAsync(request, token));
    public static readonly AgentOwnerRpcOperation<AgentTranscriptToolDetailRequest, AgentTranscriptToolDetailResult> TranscriptToolDetail =
        AgentOwnerRuntimeRpc.CreateOperation<AgentTranscriptToolDetailRequest, AgentTranscriptToolDetailResult, Wire.AgentTranscriptToolDetailRequest, Wire.AgentTranscriptToolDetailResult>(
            static (client, request, token) => client.TranscriptToolDetailAsync(request, token));
    public static readonly AgentOwnerRpcStream<HistorySearchStatusSubscription, HistorySearchStatus> HistoryStatus =
        AgentOwnerRuntimeRpc.CreateStream<HistorySearchStatusSubscription, HistorySearchStatus, Wire.HistorySearchStatusSubscription, Wire.HistorySearchStatus>(
            static (client, request, token) => client.HistoryStatusAsync(request, token));
}

internal sealed record AgentTransportProfileRequest(
    int RequestValueBytes,
    int ResponseValueBytes,
    int EventValueBytes,
    int ContentReferenceBytes,
    bool RuntimeReported);

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

internal sealed record AgentChatSnapshotRequest(
    int InitialTranscriptLimit = 60,
    string? PreferredProfileId = null,
    string? PreferredWorkspaceId = null,
    Guid? PreferredSessionId = null,
    bool IncludeInitialTranscript = true);
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

internal interface IAgentDashboardLoader
{
    event Action? DashboardInvalidated;

    Task<AgentDashboardProjection> LoadDashboardAsync(
        CancellationToken cancellationToken = default);
}

internal sealed record AgentSessionSnapshot(AgentSessionRecord Session, AgentRunCheckpointRecord? Checkpoint);

internal enum AgentTranscriptPageDirection { Recent, Before, After, Turn }
internal sealed record AgentTranscriptPageRequest(
    Guid SessionId,
    AgentTranscriptPageDirection Direction,
    int Limit = 60,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null);
internal sealed record AgentTranscriptPage(
    long Revision,
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasMore,
    TranscriptPageCursor? Continuation = null);
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

internal interface IAgentCatalogLoader
{
    Task<AgentCatalogProjection> LoadCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken = default);
}

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
internal sealed record AgentRunCommandResult(
    long Revision,
    AgentRunCheckpointRecord? Checkpoint,
    Guid? RunId = null,
    Guid? UserTurnId = null);
internal sealed record AgentRunCommandStatusRequest(Guid SessionId, Guid UserTurnId);
internal enum AgentRunCommandStatus { Pending, Committed, Absent }
internal sealed record AgentRunCommandStatusResult(long Revision, AgentRunCommandStatus Status);

internal enum AgentPermissionCommandKind { Read, SetUnrestricted, SaveOverride, DeleteOverride }
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

internal sealed record AgentAttachmentUploadDescriptor(
    string FileName,
    string? MediaType,
    int SizeBytes,
    string Sha256);
internal sealed record AgentAttachmentUploadHandle(string TransferId);
internal sealed record AgentAttachmentContentUploadRequest(
    AgentAttachmentUploadDescriptor Upload,
    SunderRpcContentReference Content);
internal sealed record AgentAttachmentContentUploadResult(AgentAttachmentUploadHandle Handle);
internal sealed record AgentAttachmentContentDownloadRequest(AgentAttachmentMetadata Metadata);
internal sealed record AgentAttachmentContentDownloadResult(SunderRpcContentReference Content);
internal sealed record AgentAttachmentContentDiscardRequest(AgentAttachmentUploadHandle Handle);
internal sealed record AgentAttachmentContentDiscardResult(bool Discarded);

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

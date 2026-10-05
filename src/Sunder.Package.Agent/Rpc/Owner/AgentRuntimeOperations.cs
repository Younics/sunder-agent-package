using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;

namespace Sunder.Package.Agent.Runtime;

// Presentation operation identities have no wire client or transport delegates.
// The role-local AgentOwnerRpcClient maps these typed tokens to generated calls.
internal sealed class AgentOwnerRpcOperation<TRequest, TResponse>
    where TRequest : class where TResponse : class;

internal sealed class AgentOwnerRpcStream<TRequest, TEvent>
    where TRequest : class where TEvent : class;

internal static class AgentRuntimeOperations
{
    public static readonly AgentOwnerRpcOperation<AgentTransportProfileRequest, AgentTransportProfile> TransportProfile = new();
    public static readonly AgentOwnerRpcOperation<AgentChatSnapshotRequest, AgentChatSnapshotProjection> ChatSnapshot = new();
    public static readonly AgentOwnerRpcOperation<AgentDashboardRequest, AgentDashboardProjection> Dashboard = new();
    public static readonly AgentOwnerRpcOperation<AgentTranscriptPageRequest, AgentTranscriptPage> Transcript = new();
    public static readonly AgentOwnerRpcOperation<AgentCatalogRequest, AgentCatalogProjection> Catalog = new();
    public static readonly AgentOwnerRpcOperation<AgentProfileCommand, AgentProfileCommandResult> Profiles = new();
    public static readonly AgentOwnerRpcOperation<AgentWorkspaceCommand, AgentWorkspaceCommandResult> Workspaces = new();
    public static readonly AgentOwnerRpcOperation<AgentSessionCommand, AgentSessionCommandResult> SessionCommands = new();
    public static readonly AgentOwnerRpcOperation<AgentRunCommand, AgentRunCommandResult> Runs = new();
    public static readonly AgentOwnerRpcOperation<AgentRunCommandStatusRequest, AgentRunCommandStatusResult> RunStatus = new();
    public static readonly AgentOwnerRpcOperation<AgentPermissionCommand, AgentPermissionProjection> Permissions = new();
    public static readonly AgentOwnerRpcOperation<AgentAttachmentContentUploadRequest, AgentAttachmentContentUploadResult> AttachmentUploads = new();
    public static readonly AgentOwnerRpcOperation<AgentAttachmentContentDownloadRequest, AgentAttachmentContentDownloadResult> AttachmentDownloads = new();
    public static readonly AgentOwnerRpcOperation<AgentAttachmentContentDiscardRequest, AgentAttachmentContentDiscardResult> AttachmentDiscards = new();
    public static readonly AgentOwnerRpcStream<AgentChangeSubscription, AgentRuntimeChange> Changes = new();
    public static readonly AgentOwnerRpcOperation<HistorySearchRequest, HistorySearchResponse> HistorySearch = new();
    public static readonly AgentOwnerRpcOperation<HistorySearchStateRequest, HistorySearchState> HistoryState = new();
    public static readonly AgentOwnerRpcOperation<HistorySearchCommand, HistorySearchCommandResult> HistoryCommands = new();
    public static readonly AgentOwnerRpcOperation<AgentTranscriptAroundTurnRequest, AgentTranscriptAroundTurnPage> TranscriptAround = new();
    public static readonly AgentOwnerRpcOperation<AgentTranscriptToolDetailRequest, AgentTranscriptToolDetailResult> TranscriptToolDetail = new();
    public static readonly AgentOwnerRpcStream<HistorySearchStatusSubscription, HistorySearchStatus> HistoryStatus = new();
}

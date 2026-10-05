using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime;

internal interface IAgentOwnerRuntimeProvider
{
    ValueTask<AgentTransportProfile> NegotiateTransportProfileAsync(
        AgentTransportProfileRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentDashboardProjection> GetDashboardAsync(
        AgentDashboardRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentCatalogProjection> GetCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(
        AgentProfileCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(
        AgentWorkspaceCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(
        AgentSessionCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(
        AgentRunCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(
        AgentRunCommandStatusRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(
        AgentPermissionCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentAttachmentContentUploadResult> UploadAttachmentAsync(
        SunderRpcInvocationContext context,
        AgentAttachmentContentUploadRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentAttachmentContentDownloadResult> DownloadAttachmentAsync(
        SunderRpcInvocationContext context,
        AgentAttachmentContentDownloadRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentAttachmentContentDiscardResult> DiscardAttachmentAsync(
        AgentAttachmentContentDiscardRequest request,
        CancellationToken cancellationToken);

    ValueTask<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken);

    ValueTask<HistorySearchState> GetHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken);

    ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand request,
        CancellationToken cancellationToken);

    ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentTranscriptToolDetailResult> GetTranscriptToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(
        AgentChangeSubscription request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(
        HistorySearchStatusSubscription request,
        CancellationToken cancellationToken);
}

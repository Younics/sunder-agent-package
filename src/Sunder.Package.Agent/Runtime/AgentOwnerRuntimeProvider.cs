using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentOwnerRuntimeProvider(
    AgentChatSnapshotHandler chatSnapshot,
    AgentDashboardHandler dashboard,
    AgentTranscriptPageHandler transcript,
    AgentCatalogHandler catalog,
    AgentProfileCommandHandler profiles,
    AgentWorkspaceCommandHandler workspaces,
    AgentSessionCommandHandler sessions,
    AgentRunCommandHandler runs,
    AgentPermissionCommandHandler permissions,
    AgentAttachmentTransferHandler attachments,
    AgentHistorySearchHandler historySearch,
    AgentHistoryStateHandler historyState,
    AgentHistoryCommandHandler historyCommands,
    AgentTranscriptAroundTurnHandler transcriptAround,
    AgentTranscriptToolDetailHandler transcriptToolDetail,
    AgentRuntimeChangeHub changes,
    AgentHistoryStatusStream historyStatus,
    AgentRuntimeTransportBudgetState transportBudgets) : IAgentOwnerRuntimeProvider
{
    public ValueTask<AgentTransportProfile> NegotiateTransportProfileAsync(
        AgentTransportProfileRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(transportBudgets.Negotiate(request));
    }

    public ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken)
        => FitAsync(chatSnapshot.HandleAsync(request, cancellationToken));

    public ValueTask<AgentDashboardProjection> GetDashboardAsync(
        AgentDashboardRequest request,
        CancellationToken cancellationToken)
        => FitAsync(dashboard.HandleAsync(request, cancellationToken));

    public ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken)
        => FitAsync(transcript.HandleAsync(request, cancellationToken));

    public ValueTask<AgentCatalogProjection> GetCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken)
        => FitAsync(catalog.HandleAsync(request, cancellationToken));

    public ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(
        AgentProfileCommand request,
        CancellationToken cancellationToken)
        => FitAsync(profiles.HandleAsync(request, cancellationToken));

    public ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(
        AgentWorkspaceCommand request,
        CancellationToken cancellationToken)
        => FitAsync(workspaces.HandleAsync(request, cancellationToken));

    public ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(
        AgentSessionCommand request,
        CancellationToken cancellationToken)
        => FitAsync(sessions.HandleAsync(request, cancellationToken));

    public ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(
        AgentRunCommand request,
        CancellationToken cancellationToken)
        => FitAsync(runs.HandleAsync(request, cancellationToken));

    public ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(
        AgentRunCommandStatusRequest request,
        CancellationToken cancellationToken)
        => FitAsync(runs.HandleAsync(request, cancellationToken));

    public ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(
        AgentPermissionCommand request,
        CancellationToken cancellationToken)
        => FitAsync(permissions.HandleAsync(request, cancellationToken));

    public ValueTask<AgentAttachmentContentUploadResult> UploadAttachmentAsync(
        SunderRpcInvocationContext context,
        AgentAttachmentContentUploadRequest request,
        CancellationToken cancellationToken)
        => FitAsync(attachments.UploadAsync(context, request, cancellationToken));

    public ValueTask<AgentAttachmentContentDownloadResult> DownloadAttachmentAsync(
        SunderRpcInvocationContext context,
        AgentAttachmentContentDownloadRequest request,
        CancellationToken cancellationToken)
        => FitAsync(attachments.DownloadAsync(context, request, cancellationToken));

    public ValueTask<AgentAttachmentContentDiscardResult> DiscardAttachmentAsync(
        AgentAttachmentContentDiscardRequest request,
        CancellationToken cancellationToken)
        => FitAsync(attachments.DiscardAsync(request, cancellationToken));

    public ValueTask<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken)
        => FitAsync(historySearch.HandleAsync(request, cancellationToken));

    public ValueTask<HistorySearchState> GetHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken)
        => FitAsync(historyState.HandleAsync(request, cancellationToken));

    public ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand request,
        CancellationToken cancellationToken)
        => FitAsync(historyCommands.HandleAsync(request, cancellationToken));

    public ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken)
        => FitAsync(transcriptAround.HandleAsync(request, cancellationToken));

    public ValueTask<AgentTranscriptToolDetailResult> GetTranscriptToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken)
        => FitAsync(transcriptToolDetail.HandleAsync(request, cancellationToken));

    public IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(
        AgentChangeSubscription request,
        CancellationToken cancellationToken)
        => changes.SubscribeAsync(request, cancellationToken);

    public IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(
        HistorySearchStatusSubscription request,
        CancellationToken cancellationToken)
        => historyStatus.SubscribeAsync(request, cancellationToken);

    private static async ValueTask<T> AwaitAsync<T>(ValueTask<T> pending)
        where T : class
        => await pending.ConfigureAwait(false);

    private async ValueTask<T> FitAsync<T>(ValueTask<T> pending)
        where T : class
    {
        var response = await AwaitAsync(pending).ConfigureAwait(false);
        AgentRuntimePayloadLimits.EnsureResponseFits(response, transportBudgets.Current);
        return response;
    }
}

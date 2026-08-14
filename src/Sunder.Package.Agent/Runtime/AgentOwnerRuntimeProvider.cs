using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;

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
    AgentHistoryStatusStream historyStatus) : IAgentOwnerRuntimeProvider
{
    public ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken)
        => chatSnapshot.HandleAsync(request, cancellationToken);

    public ValueTask<AgentDashboardProjection> GetDashboardAsync(
        AgentDashboardRequest request,
        CancellationToken cancellationToken)
        => dashboard.HandleAsync(request, cancellationToken);

    public ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken)
        => transcript.HandleAsync(request, cancellationToken);

    public ValueTask<AgentCatalogProjection> GetCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken)
        => catalog.HandleAsync(request, cancellationToken);

    public ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(
        AgentProfileCommand request,
        CancellationToken cancellationToken)
        => profiles.HandleAsync(request, cancellationToken);

    public ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(
        AgentWorkspaceCommand request,
        CancellationToken cancellationToken)
        => workspaces.HandleAsync(request, cancellationToken);

    public ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(
        AgentSessionCommand request,
        CancellationToken cancellationToken)
        => sessions.HandleAsync(request, cancellationToken);

    public ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(
        AgentRunCommand request,
        CancellationToken cancellationToken)
        => runs.HandleAsync(request, cancellationToken);

    public ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(
        AgentRunCommandStatusRequest request,
        CancellationToken cancellationToken)
        => runs.HandleAsync(request, cancellationToken);

    public ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(
        AgentPermissionCommand request,
        CancellationToken cancellationToken)
        => permissions.HandleAsync(request, cancellationToken);

    public ValueTask<AgentAttachmentTransferResult> TransferAttachmentAsync(
        AgentAttachmentTransferRequest request,
        CancellationToken cancellationToken)
        => attachments.HandleAsync(request, cancellationToken);

    public ValueTask<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken)
        => historySearch.HandleAsync(request, cancellationToken);

    public ValueTask<HistorySearchState> GetHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken)
        => historyState.HandleAsync(request, cancellationToken);

    public ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand request,
        CancellationToken cancellationToken)
        => historyCommands.HandleAsync(request, cancellationToken);

    public ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken)
        => transcriptAround.HandleAsync(request, cancellationToken);

    public ValueTask<AgentTranscriptToolDetailResponse> GetTranscriptToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken)
        => transcriptToolDetail.HandleAsync(request, cancellationToken);

    public IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(
        AgentChangeSubscription request,
        CancellationToken cancellationToken)
        => changes.SubscribeAsync(request, cancellationToken);

    public IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(
        HistorySearchStatusSubscription request,
        CancellationToken cancellationToken)
        => historyStatus.SubscribeAsync(request, cancellationToken);
}

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using System.Runtime.CompilerServices;

namespace Sunder.Package.Agent.Runtime;

[SunderService(Key = "owner-runtime", ContractId = "sunder.agent.owner.runtime", ServiceId = "agent-runtime")]
[SunderServiceProvider(ProviderId = "agent.owner.runtime", Audience = SunderServiceAudience.Owner)]
[SunderServiceError(OperationFailed, Description = "The requested Agent operation could not be completed.")]
internal sealed partial class AgentOwnerRuntimeProvider(
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
    AgentToolService tools,
    AgentRuntimeTransportBudgetState transportBudgets)
{
    private const string OperationFailed = "agent.owner.operation-failed";

    [SunderMethod(MethodId = "transport-profile", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTransportProfile> NegotiateTransportProfileAsync(
        AgentTransportProfileRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return ValueTask.FromResult(transportBudgets.Negotiate(request)); }
        catch (AgentOwnerOperationException exception) { throw Map(exception); }
    }

    [SunderMethod(MethodId = "chat-snapshot", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => chatSnapshot.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "dashboard", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentDashboardProjection> GetDashboardAsync(
        AgentDashboardRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => dashboard.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "transcript-page", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => transcript.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "catalog", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentCatalogProjection> GetCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => catalog.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "profile-command", Errors = [OperationFailed])]
    public ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(
        AgentProfileCommand request,
        CancellationToken cancellationToken)
        => FitAsync(() => profiles.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "workspace-command", Errors = [OperationFailed])]
    public ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(
        AgentWorkspaceCommand request,
        CancellationToken cancellationToken)
        => FitAsync(() => workspaces.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "session-command", Errors = [OperationFailed])]
    public ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(
        AgentSessionCommand request,
        CancellationToken cancellationToken)
        => FitAsync(() => sessions.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "run-command", Errors = [OperationFailed])]
    public ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(
        AgentRunCommand request,
        CancellationToken cancellationToken)
        => FitAsync(() => runs.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "run-status", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(
        AgentRunCommandStatusRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => runs.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "permission-command", Errors = [OperationFailed])]
    public ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(
        AgentPermissionCommand request,
        CancellationToken cancellationToken)
        => FitAsync(() => permissions.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "attachment-upload", Errors = [OperationFailed])]
    public ValueTask<AgentAttachmentContentUploadResult> UploadAttachmentAsync(
        AgentAttachmentContentUploadRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => attachments.UploadAsync(request, cancellationToken));

    [SunderMethod(MethodId = "attachment-download", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentAttachmentContentDownloadResult> DownloadAttachmentAsync(
        AgentAttachmentContentDownloadRequest request,
        CancellationToken cancellationToken)
    {
        // This response is a single SDK content resource, not inline attachment
        // bytes. The binding enforces framing limits; the receiver verifies the
        // attachment's declared byte/digest budget while consuming the resource.
        try { return attachments.DownloadAsync(request, cancellationToken); }
        catch (AgentOwnerOperationException exception) { throw Map(exception); }
    }

    [SunderMethod(MethodId = "attachment-discard", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentAttachmentContentDiscardResult> DiscardAttachmentAsync(
        AgentAttachmentContentDiscardRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => attachments.DiscardAsync(request, cancellationToken));

    [SunderMethod(MethodId = "history-search", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => historySearch.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "history-state", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<HistorySearchState> GetHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => historyState.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "history-command", Errors = [OperationFailed])]
    public ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand request,
        CancellationToken cancellationToken)
        => FitAsync(() => historyCommands.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "transcript-around", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => transcriptAround.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "transcript-tool-detail", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public ValueTask<AgentTranscriptToolDetailResult> GetTranscriptToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken)
        => FitAsync(() => transcriptToolDetail.HandleAsync(request, cancellationToken));

    [SunderMethod(MethodId = "tool-presentation", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public async ValueTask<AgentToolPresentation?> GetToolPresentationAsync(
        AgentToolPresentationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var presentation = await tools.ResolvePresentationAsync(request, cancellationToken).ConfigureAwait(false);
            if (presentation is not null)
                AgentRuntimePayloadLimits.EnsureResponseFits(presentation, transportBudgets.Current);
            return presentation;
        }
        catch (AgentOwnerOperationException exception) { throw Map(exception); }
    }

    [SunderMethod(MethodId = "changes", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(
        AgentChangeSubscription request,
        CancellationToken cancellationToken)
        => ObserveAsync(changes.SubscribeAsync(request, cancellationToken), cancellationToken);

    [SunderMethod(MethodId = "history-status", Idempotency = SunderServiceIdempotency.Idempotent, Errors = [OperationFailed])]
    public IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(
        HistorySearchStatusSubscription request,
        CancellationToken cancellationToken)
        => ObserveAsync(historyStatus.SubscribeAsync(request, cancellationToken), cancellationToken);

    private async ValueTask<T> FitAsync<T>(Func<ValueTask<T>> operation)
        where T : class
    {
        try
        {
            var response = await operation().ConfigureAwait(false);
            AgentRuntimePayloadLimits.EnsureResponseFits(response, transportBudgets.Current);
            return response;
        }
        catch (AgentOwnerOperationException exception) { throw Map(exception); }
    }

    private static async IAsyncEnumerable<T> ObserveAsync<T>(IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var events = source.GetAsyncEnumerator(cancellationToken);
        while (await NextAsync(events).ConfigureAwait(false)) yield return events.Current;
    }

    private static async ValueTask<bool> NextAsync<T>(IAsyncEnumerator<T> events)
    {
        try { return await events.MoveNextAsync().ConfigureAwait(false); }
        catch (AgentOwnerOperationException exception) { throw Map(exception); }
    }

    private static SunderRpcException Map(AgentOwnerOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message) ? "The requested Agent operation failed." : exception.Message;
        return new(new(SunderRpcErrorKind.Domain, OperationFailed, message.Length > 2048 ? message[..2048] : message));
    }
}

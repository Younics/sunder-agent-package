using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway :
    IAgentHistorySearchGateway,
    IAgentTranscriptAnchorGateway
{
    private int _historyObservationStarted;
    private readonly object _historyStatusLock = new();
    private long _historyStatusRevision;
    private string? _historyRuntimeInstanceId;

    public event Action<HistorySearchStatus>? HistoryStatusChanged;
    public event Action? HistoryRuntimeChanged;

    public async Task<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(AgentRuntimeOperations.HistorySearch, request, cancellationToken)
            .ConfigureAwait(false);
        ObserveHistoryStatus(response.Status, publishStatus: false);
        return response;
    }

    public async Task<HistorySearchState> LoadHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = await InvokeAsync(AgentRuntimeOperations.HistoryState, request, cancellationToken)
            .ConfigureAwait(false);
        ObserveHistoryStatus(state.Status, publishStatus: false);
        return state;
    }

    public async Task<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(AgentRuntimeOperations.HistoryCommands, command, cancellationToken)
            .ConfigureAwait(false);
        ObserveHistoryStatus(result.Status, publishStatus: false);
        return result;
    }

    public async Task<AgentTranscriptAroundTurnPage> LoadTranscriptAroundTurnAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await InvokeAsync(AgentRuntimeOperations.TranscriptAround, request, cancellationToken)
            .ConfigureAwait(false);
        CacheTurns(page.Turns);
        return page;
    }

    public async Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(
                AgentRuntimeOperations.TranscriptToolDetail,
                request,
                cancellationToken)
            .ConfigureAwait(false)).Detail;

    public void StartObservingHistoryStatus()
    {
        if (Interlocked.Exchange(ref _historyObservationStarted, 1) == 0)
        {
            _ = ObserveHistoryStatusAsync(_lifetime.Token);
        }
    }

    private async Task ObserveHistoryStatusAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long afterRevision;
                string? runtimeInstanceId;
                lock (_historyStatusLock)
                {
                    afterRevision = _historyStatusRevision;
                    runtimeInstanceId = _historyRuntimeInstanceId;
                }
                await foreach (var status in _transport.SubscribeAsync(
                                   AgentRuntimeOperations.HistoryStatus,
                                   new HistorySearchStatusSubscription(afterRevision, runtimeInstanceId),
                                   cancellationToken).ConfigureAwait(false))
                {
                    ObserveHistoryStatus(status, publishStatus: true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!await DelayForReconnectAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    private void ObserveHistoryStatus(HistorySearchStatus status, bool publishStatus)
    {
        var runtimeChanged = false;
        var accepted = false;
        lock (_historyStatusLock)
        {
            runtimeChanged = !string.IsNullOrWhiteSpace(_historyRuntimeInstanceId)
                             && !string.IsNullOrWhiteSpace(status.RuntimeInstanceId)
                             && !string.Equals(
                                 _historyRuntimeInstanceId,
                                 status.RuntimeInstanceId,
                                 StringComparison.Ordinal);
            if (runtimeChanged)
            {
                _historyStatusRevision = 0;
            }
            if (!runtimeChanged
                && string.Equals(
                    _historyRuntimeInstanceId,
                    status.RuntimeInstanceId,
                    StringComparison.Ordinal)
                && status.Revision < _historyStatusRevision)
            {
                return;
            }
            _historyRuntimeInstanceId = status.RuntimeInstanceId;
            _historyStatusRevision = status.Revision;
            accepted = true;
        }
        if (!accepted)
        {
            return;
        }
        if (publishStatus)
        {
            Raise(HistoryStatusChanged, status);
        }
        if (runtimeChanged)
        {
            Raise(HistoryRuntimeChanged);
        }
    }
}

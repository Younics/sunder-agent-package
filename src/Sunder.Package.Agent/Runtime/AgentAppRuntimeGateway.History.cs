using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway :
    IAgentHistorySearchGateway,
    IAgentTranscriptAnchorGateway
{
    private static readonly TimeSpan HistoryReconnectDelay = TimeSpan.FromSeconds(1);
    private int _historyObservationStarted;
    private readonly object _historyStatusLock = new();
    private long _historyStatusRevision;
    private string? _historyRuntimeInstanceId;
    private Task? _historyObservationTask;

    public event Action<HistorySearchStatus>? HistoryStatusChanged;
    public event Action? HistoryRuntimeChanged;

    public async Task<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(_owner.SearchHistoryAsync, request, cancellationToken)
            .ConfigureAwait(false);
        ObserveHistoryStatus(response.Status, publishStatus: false);
        return response;
    }

    public async Task<HistorySearchState> LoadHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = await InvokeAsync(_owner.GetHistoryStateAsync, request, cancellationToken)
            .ConfigureAwait(false);
        ObserveHistoryStatus(state.Status, publishStatus: false);
        return state;
    }

    public async Task<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(_owner.ExecuteHistoryCommandAsync, command, cancellationToken)
            .ConfigureAwait(false);
        ObserveHistoryStatus(result.Status, publishStatus: false);
        return result;
    }

    public async Task<AgentTranscriptAroundTurnPage> LoadTranscriptAroundTurnAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await InvokeAsync(_owner.GetTranscriptAroundAsync, request, cancellationToken)
            .ConfigureAwait(false);
        CacheTurns(page.Turns);
        return page;
    }

    public async Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(
                _owner.GetTranscriptToolDetailAsync,
                request,
                cancellationToken)
            .ConfigureAwait(false)).Detail;

    public void StartObservingHistoryStatus()
    {
        lock (_producerTaskLock)
        {
            if (_disposed || Interlocked.Exchange(ref _historyObservationStarted, 1) != 0)
            {
                return;
            }

            _historyObservationTask = Task.Run(() => ObserveHistoryStatusAsync(_lifetime.Token));
            RetainProducerTaskCore(_historyObservationTask);
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
                if (string.IsNullOrWhiteSpace(runtimeInstanceId))
                {
                    lock (_observationLock)
                    {
                        runtimeInstanceId = _runtimeInstanceId;
                    }
                }
                var subscription = new HistorySearchStatusSubscription(afterRevision, runtimeInstanceId);
                await _resources.PrepareAsync(subscription, cancellationToken).ConfigureAwait(false);
                await foreach (var status in _owner.WatchHistoryStatusAsync(
                                   subscription,
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
                // Reconnect below after the same backoff used for a clean stream end.
            }
            if (!await DelayForReconnectAsync(HistoryReconnectDelay, cancellationToken).ConfigureAwait(false))
            {
                return;
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

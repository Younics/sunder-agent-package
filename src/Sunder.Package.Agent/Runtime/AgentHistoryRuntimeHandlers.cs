using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentHistorySearchHandler(HistorySearchService search)
    : IPackageRuntimeOperationHandler<HistorySearchRequest, HistorySearchResponse>
{
    public async ValueTask<HistorySearchResponse> HandleAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken = default)
        => await search.SearchAsync(request, cancellationToken).ConfigureAwait(false);
}

internal sealed class AgentHistoryStateHandler(HistorySearchService search)
    : IPackageRuntimeOperationHandler<HistorySearchStateRequest, HistorySearchState>
{
    public async ValueTask<HistorySearchState> HandleAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken = default)
        => await search.GetStateAsync(request, cancellationToken).ConfigureAwait(false);
}

internal sealed class AgentHistoryCommandHandler(HistorySearchService search)
    : IPackageRuntimeOperationHandler<HistorySearchCommand, HistorySearchCommandResult>
{
    public async ValueTask<HistorySearchCommandResult> HandleAsync(
        HistorySearchCommand request,
        CancellationToken cancellationToken = default)
        => await search.ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(false);
}

internal sealed class AgentTranscriptAroundTurnHandler(
    AgentLocalStore store,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentTranscriptAroundTurnRequest, AgentTranscriptAroundTurnPage>
{
    public ValueTask<AgentTranscriptAroundTurnPage> HandleAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = store.GetSession(request.SessionId)
            ?? throw new InvalidOperationException("The requested root session is no longer available.");
        if (session.ParentSessionId is not null)
        {
            throw new InvalidOperationException("Child-session transcript anchors must be opened in Subsessions.");
        }
        var page = store.LoadTranscriptAroundTurn(
            request.SessionId,
            request.TurnId,
            Math.Clamp(request.BeforeLimit, 0, HistorySearchLimits.AroundTurnSideLimit),
            Math.Clamp(request.AfterLimit, 0, HistorySearchLimits.AroundTurnSideLimit),
            changes.Revision);
        return ValueTask.FromResult(AgentRuntimePayloadLimits.FitAroundTurnPage(page, request.ItemId));
    }
}

internal sealed class AgentHistoryStatusStream :
    IPackageRuntimeStreamHandler<HistorySearchStatusSubscription, HistorySearchStatus>,
    IDisposable
{
    private readonly HistorySearchRuntimeState _state;
    private readonly object _lock = new();
    private readonly Dictionary<long, Channel<HistorySearchStatus>> _subscribers = [];
    private long _nextSubscriberId;
    private bool _disposed;

    internal AgentHistoryStatusStream(HistorySearchRuntimeState state)
    {
        _state = state;
        state.Changed += OnChanged;
    }

    public async IAsyncEnumerable<HistorySearchStatus> SubscribeAsync(
        HistorySearchStatusSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = Channel.CreateBounded<HistorySearchStatus>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var id = Interlocked.Increment(ref _nextSubscriberId);
        lock (_lock)
        {
            _subscribers[id] = channel;
        }
        try
        {
            var current = _state.Current;
            if (!string.Equals(
                    current.RuntimeInstanceId,
                    request.RuntimeInstanceId,
                    StringComparison.Ordinal)
                || current.Revision > request.AfterRevision)
            {
                yield return current;
            }
            await foreach (var status in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return status;
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(id);
            }
            channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _state.Changed -= OnChanged;
        lock (_lock)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }

    private void OnChanged(HistorySearchStatus status)
    {
        lock (_lock)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryWrite(status);
            }
        }
    }
}

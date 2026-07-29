namespace Sunder.Package.Agent.Shared.Presentation;

internal readonly record struct LatestRequestTicket(
    long CoordinatorId,
    string Channel,
    long Generation,
    CancellationToken CancellationToken);

internal sealed class LatestRequestCoordinator : IDisposable
{
    private static long _nextCoordinatorId;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, RequestState> _requests = new(StringComparer.Ordinal);
    private readonly long _coordinatorId = Interlocked.Increment(ref _nextCoordinatorId);
    private bool _disposed;

    public LatestRequestTicket Begin(string channel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        CancellationTokenSource? superseded;
        LatestRequestTicket ticket;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requests.TryGetValue(channel, out var previous);
            superseded = previous?.Cancellation;
            var generation = (previous?.Generation ?? 0) + 1;
            var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _requests[channel] = new RequestState(generation, current);
            ticket = new LatestRequestTicket(_coordinatorId, channel, generation, current.Token);
        }

        CancelAndDispose(superseded);
        return ticket;
    }

    public bool IsCurrent(LatestRequestTicket ticket)
    {
        lock (_syncRoot)
        {
            return IsLatestCore(ticket)
                   && !_requests[ticket.Channel].Cancellation!.IsCancellationRequested;
        }
    }

    public bool IsLatest(LatestRequestTicket ticket)
    {
        lock (_syncRoot)
        {
            return IsLatestCore(ticket);
        }
    }

    public bool Complete(LatestRequestTicket ticket)
    {
        CancellationTokenSource? cancellation = null;
        lock (_syncRoot)
        {
            if (!IsLatestCore(ticket))
            {
                return false;
            }

            cancellation = _requests[ticket.Channel].Cancellation;
            _requests[ticket.Channel] = new RequestState(ticket.Generation, null);
        }

        cancellation!.Dispose();
        return true;
    }

    public void Invalidate(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _requests.TryGetValue(channel, out var previous);
            cancellation = previous?.Cancellation;
            _requests[channel] = new RequestState((previous?.Generation ?? 0) + 1, null);
        }

        CancelAndDispose(cancellation);
    }

    public void Dispose()
    {
        CancellationTokenSource[] cancellations;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellations = _requests.Values
                .Select(static request => request.Cancellation)
                .Where(static cancellation => cancellation is not null)
                .Cast<CancellationTokenSource>()
                .ToArray();
            _requests.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            CancelAndDispose(cancellation);
        }
    }

    private bool IsLatestCore(LatestRequestTicket ticket)
        => !_disposed
           && ticket.CoordinatorId == _coordinatorId
           && _requests.TryGetValue(ticket.Channel, out var current)
           && current.Generation == ticket.Generation
           && current.Cancellation is not null;

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private sealed record RequestState(long Generation, CancellationTokenSource? Cancellation);
}

internal sealed class SerializedRefreshLoop : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly Action<Exception>? _failureHandler;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _loop = Task.CompletedTask;
    private CancellationTokenSource? _activeRefresh;
    private bool _dirty;
    private bool _running;
    private bool _disposed;

    public SerializedRefreshLoop(
        Func<CancellationToken, Task> refresh,
        Action<Exception>? failureHandler = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _failureHandler = failureHandler;
    }

    public Task MarkDirty()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _dirty = true;
            if (!_running)
            {
                _running = true;
                _loop = RunAsync();
            }

            return _loop;
        }
    }

    public Task WhenIdle
    {
        get
        {
            lock (_syncRoot)
            {
                return _loop;
            }
        }
    }

    public void DiscardPending()
    {
        CancellationTokenSource? activeRefresh;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _dirty = false;
            activeRefresh = _activeRefresh;
        }

        try
        {
            activeRefresh?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dirty = false;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        while (true)
        {
            CancellationTokenSource refreshCancellation;
            lock (_syncRoot)
            {
                if (_disposed || !_dirty)
                {
                    _running = false;
                    return;
                }

                _dirty = false;
                refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _activeRefresh = refreshCancellation;
            }

            try
            {
                await _refresh(refreshCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
            {
                if (_lifetime.IsCancellationRequested)
                {
                    lock (_syncRoot)
                    {
                        _running = false;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    _failureHandler?.Invoke(ex);
                }
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_activeRefresh, refreshCancellation))
                    {
                        _activeRefresh = null;
                    }
                }
                refreshCancellation.Dispose();
            }
        }
    }
}

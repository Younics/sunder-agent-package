using System.Collections.Concurrent;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerContainerLifecycleService : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ContainerLifecycleState> _states = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _disposeStopTimeout;
    private int _disposed;

    public DockerContainerLifecycleService()
        : this(TimeSpan.FromMinutes(5))
    {
    }

    public DockerContainerLifecycleService(TimeSpan idleTimeout)
        : this(idleTimeout, TimeSpan.FromSeconds(15))
    {
    }

    public DockerContainerLifecycleService(TimeSpan idleTimeout, TimeSpan disposeStopTimeout)
    {
        _idleTimeout = idleTimeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : idleTimeout;
        _disposeStopTimeout = disposeStopTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(15)
            : disposeStopTimeout;
    }

    public async Task<DockerContainerLease> AcquireAsync(
        string key,
        Func<CancellationToken, Task<string>> ensureRunningAsync,
        Func<string, CancellationToken, Task> stopAsync,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var state = _states.GetOrAdd(key, static key => new ContainerLifecycleState(key));
        state.AcquireReference(stopAsync);
        var lockHeldByLease = false;
        try
        {
            await state.OperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var containerName = await ensureRunningAsync(cancellationToken).ConfigureAwait(false);
                state.SetContainerName(containerName);
                lockHeldByLease = true;
                return new DockerContainerLease(
                    () =>
                    {
                        state.OperationLock.Release();
                        Release(state);
                    },
                    containerName);
            }
            finally
            {
                if (!lockHeldByLease)
                {
                    state.OperationLock.Release();
                }
            }
        }
        catch
        {
            Release(state);
            throw;
        }
    }

    private void Release(ContainerLifecycleState state)
    {
        var idleSignal = state.ReleaseReference(Volatile.Read(ref _disposed) == 0);
        if (idleSignal is null)
        {
            return;
        }

        _ = StopWhenIdleAsync(state, idleSignal);
    }

    private async Task StopWhenIdleAsync(ContainerLifecycleState state, CancellationTokenSource idleSignal)
    {
        try
        {
            await Task.Delay(_idleTimeout, idleSignal.Token).ConfigureAwait(false);
            var stopRequest = state.TryBeginIdleStop(idleSignal);
            if (stopRequest is null)
            {
                return;
            }

            await state.OperationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                stopRequest = state.TryBeginIdleStop(idleSignal);
                if (stopRequest is null)
                {
                    return;
                }

                await stopRequest.Value.StopAsync(stopRequest.Value.ContainerName, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                state.CompleteIdleStop(idleSignal);
                state.OperationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            state.CompleteIdleStop(idleSignal);
        }
    }

    public void Dispose()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        foreach (var state in _states.Values)
        {
            state.CancelIdleStop();
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        foreach (var state in _states.Values)
        {
            state.CancelIdleStop();
            await StopKnownContainerAsync(state).ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private bool TryBeginDispose()
        => Interlocked.Exchange(ref _disposed, 1) == 0;

    private async Task StopKnownContainerAsync(ContainerLifecycleState state)
    {
        using var timeoutCts = new CancellationTokenSource(_disposeStopTimeout);
        try
        {
            await state.OperationLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                var currentStopRequest = state.TryGetStopRequest();
                if (currentStopRequest is null)
                {
                    return;
                }

                await currentStopRequest.Value.StopAsync(currentStopRequest.Value.ContainerName, timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                state.OperationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private sealed class ContainerLifecycleState(string key)
    {
        private readonly object _syncRoot = new();
        private int _activeCount;
        private CancellationTokenSource? _idleStopCts;
        private string? _containerName;
        private Func<string, CancellationToken, Task>? _stopAsync;

        public string Key { get; } = key;

        public SemaphoreSlim OperationLock { get; } = new(1, 1);

        public void AcquireReference(Func<string, CancellationToken, Task> stopAsync)
        {
            lock (_syncRoot)
            {
                _activeCount++;
                _stopAsync = stopAsync;
                CancelIdleStopCore();
            }
        }

        public void SetContainerName(string containerName)
        {
            lock (_syncRoot)
            {
                _containerName = containerName;
            }
        }

        public CancellationTokenSource? ReleaseReference(bool scheduleIdleStop)
        {
            lock (_syncRoot)
            {
                if (_activeCount <= 0)
                {
                    return null;
                }

                _activeCount--;
                if (!scheduleIdleStop || _activeCount != 0 || string.IsNullOrWhiteSpace(_containerName) || _stopAsync is null)
                {
                    return null;
                }

                CancelIdleStopCore();
                _idleStopCts = new CancellationTokenSource();
                return _idleStopCts;
            }
        }

        public IdleStopRequest? TryBeginIdleStop(CancellationTokenSource idleStopCts)
        {
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_idleStopCts, idleStopCts)
                    || _activeCount != 0
                    || string.IsNullOrWhiteSpace(_containerName)
                    || _stopAsync is null)
                {
                    return null;
                }

                return new IdleStopRequest(_containerName, _stopAsync);
            }
        }

        public void CompleteIdleStop(CancellationTokenSource idleStopCts)
        {
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_idleStopCts, idleStopCts))
                {
                    return;
                }

                _idleStopCts.Dispose();
                _idleStopCts = null;
            }
        }

        public IdleStopRequest? TryGetStopRequest()
        {
            lock (_syncRoot)
            {
                return string.IsNullOrWhiteSpace(_containerName) || _stopAsync is null
                    ? null
                    : new IdleStopRequest(_containerName, _stopAsync);
            }
        }

        public void CancelIdleStop()
        {
            lock (_syncRoot)
            {
                CancelIdleStopCore();
            }
        }

        private void CancelIdleStopCore()
        {
            if (_idleStopCts is null)
            {
                return;
            }

            _idleStopCts.Cancel();
            _idleStopCts.Dispose();
            _idleStopCts = null;
        }
    }

    private readonly record struct IdleStopRequest(
        string ContainerName,
        Func<string, CancellationToken, Task> StopAsync);

    public sealed class DockerContainerLease : IDisposable
    {
        private readonly Action _release;
        private bool _disposed;

        internal DockerContainerLease(Action release, string containerName)
        {
            _release = release;
            ContainerName = containerName;
        }

        public string ContainerName { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _release();
        }
    }
}

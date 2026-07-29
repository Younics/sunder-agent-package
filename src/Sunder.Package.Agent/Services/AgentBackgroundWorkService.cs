using System.Threading.Channels;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentBackgroundWorkService : IPackageBackgroundService, IAsyncDisposable
{
    private const int QueueCapacity = 128;

    private readonly object _syncRoot = new();
    private readonly HashSet<Task> _activeTasks = [];
    private CancellationTokenSource _lifetime = new();
    private Channel<Func<CancellationToken, Task>> _queue = CreateQueue();
    private Task? _worker;
    private bool _stopping;
    private bool _disposed;

    internal bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return !_stopping && _worker is { IsCompleted: false };
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
            {
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
                _queue = CreateQueue();
                _stopping = false;
            }
            _worker ??= ProcessQueueAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task[] tasks;
        CancellationTokenSource lifetime;
        var cancelLifetime = false;
        lock (_syncRoot)
        {
            lifetime = _lifetime;
            if (_stopping)
            {
                var pending = _activeTasks.ToList();
                if (_worker is not null)
                {
                    pending.Add(_worker);
                }
                tasks = pending.ToArray();
            }
            else
            {
                _stopping = true;
                _queue.Writer.TryComplete();
                cancelLifetime = true;
                var pending = _activeTasks.ToList();
                if (_worker is not null)
                {
                    pending.Add(_worker);
                }
                tasks = pending.ToArray();
            }
        }

        if (cancelLifetime)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(lifetime, _lifetime))
                {
                    _worker = null;
                }
            }
        }
    }

    internal Task SignalStopAsync()
        => AgentRuntimeWorkerCancellation.SignalAsync(Volatile.Read(ref _lifetime));

    internal bool TryQueue(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_syncRoot)
        {
            return !_stopping && !_disposed && _queue.Writer.TryWrite(work);
        }
    }

    internal Task<T> RunOwnedAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        CancellationTokenSource linked;
        TaskCompletionSource start;
        TaskCompletionSource<T> completion;
        TaskCompletionSource drained;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
            {
                throw new OperationCanceledException("Agent background work is stopping.", _lifetime.Token);
            }
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            // Gate user code until its drain task is visible to concurrent shutdown.
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeTasks.Add(drained.Task);
            _ = RunOwnedCoreAsync(work, linked, start.Task, completion, drained);
            start.TrySetResult();
        }
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _lifetime.Dispose();
    }

    private static Channel<Func<CancellationToken, Task>> CreateQueue()
        => Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private async Task RunOwnedCoreAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationTokenSource linked,
        Task start,
        TaskCompletionSource<T> completion,
        TaskCompletionSource drained)
    {
        try
        {
            await start.ConfigureAwait(false);
            completion.TrySetResult(await work(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            linked.Dispose();
            drained.TrySetResult();
            lock (_syncRoot)
            {
                _activeTasks.Remove(drained.Task);
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await work(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Individual work owners log failures; one item must not stop the queue.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

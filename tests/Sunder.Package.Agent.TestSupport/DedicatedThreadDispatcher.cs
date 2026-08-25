using System.Collections.Concurrent;

namespace Sunder.Package.Agent.TestSupport;

public sealed class DedicatedThreadDispatcher : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly ManualResetEventSlim _enabled = new(initialState: true);
    private readonly AsyncCheckpoint _posted = new();
    private readonly AsyncCheckpoint _entered = new();
    private readonly AsyncCheckpoint _completed = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource<int> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public DedicatedThreadDispatcher(string? threadName = null)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName ?? "Sunder Agent test dispatcher",
        };
        _thread.Start();
        ThreadId = _started.Task.GetAwaiter().GetResult();
    }

    public int ThreadId { get; }

    public int PostedCount => _posted.Count;

    public int EnteredCount => _entered.Count;

    public int CompletedCount => _completed.Count;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

    public Task InvokeAsync(Action action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(action, completion));
        _posted.Signal();
        return completion.Task;
    }

    public void Pause() => _enabled.Reset();

    public void Resume() => _enabled.Set();

    public Task WaitForPostedAsync(int count, CancellationToken cancellationToken = default)
        => _posted.WaitForCountAsync(count, cancellationToken);

    public Task WaitForEnteredAsync(int count, CancellationToken cancellationToken = default)
        => _entered.WaitForCountAsync(count, cancellationToken);

    public Task WaitForCompletedAsync(int count, CancellationToken cancellationToken = default)
        => _completed.WaitForCountAsync(count, cancellationToken);

    public Task WaitForIdleAsync() => InvokeAsync(static () => { });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Resume();
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
        _enabled.Dispose();
    }

    private void Run()
    {
        _started.TrySetResult(Environment.CurrentManagedThreadId);
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            _enabled.Wait();
            _entered.Signal();
            try
            {
                item.Action();
                item.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
            finally
            {
                _completed.Signal();
            }
        }
    }

    private sealed record WorkItem(Action Action, TaskCompletionSource Completion);
}

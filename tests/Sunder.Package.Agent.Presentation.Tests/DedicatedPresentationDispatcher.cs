using System.Collections.Concurrent;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class DedicatedPresentationDispatcher : IPresentationDispatcher, IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly ManualResetEventSlim _enabled = new(initialState: true);
    private readonly SemaphoreSlim _enqueued = new(0);
    private readonly Thread _thread;
    private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DedicatedPresentationDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Presentation test dispatcher",
        };
        _thread.Start();
        ThreadId = _started.Task.GetAwaiter().GetResult();
    }

    public int ThreadId { get; }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

    public Task InvokeAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(action, completion));
        _enqueued.Release();
        return completion.Task;
    }

    public void Pause() => _enabled.Reset();

    public void Resume() => _enabled.Set();

    public Task WaitForEnqueuedAsync() => _enqueued.WaitAsync();

    public Task WaitForIdleAsync() => InvokeAsync(() => { });

    private void Run()
    {
        _started.SetResult(Environment.CurrentManagedThreadId);
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            _enabled.Wait();
            try
            {
                item.Action();
                item.Completion.SetResult();
            }
            catch (Exception ex)
            {
                item.Completion.SetException(ex);
            }
        }
    }

    public void Dispose()
    {
        Resume();
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
        _enabled.Dispose();
        _enqueued.Dispose();
    }

    private sealed record WorkItem(Action Action, TaskCompletionSource Completion);
}

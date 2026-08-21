namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private readonly object _producerTaskLock = new();
    private readonly HashSet<Task> _retainedProducerTasks = [];
    private TaskCompletionSource? _disposalCompletion;
    private volatile bool _disposed;

    private Task<T> StartRetainedProducerTask<T>(Func<Task<T>> start)
    {
        lock (_producerTaskLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = start();
            RetainProducerTaskCore(task);
            return task;
        }
    }

    private void RetainProducerTaskCore(Task task)
    {
        _retainedProducerTasks.Add(task);
        _ = task.ContinueWith(
            static (completed, state) =>
                ((AgentAppRuntimeGateway)state!).OnRetainedProducerTaskCompleted(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnRetainedProducerTaskCompleted(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
        lock (_producerTaskLock)
        {
            _retainedProducerTasks.Remove(task);
        }
    }

    public void Dispose()
        => _ = BeginDispose();

    public ValueTask DisposeAsync()
        => new(BeginDispose());

    private Task BeginDispose()
    {
        TaskCompletionSource completion;
        lock (_producerTaskLock)
        {
            if (_disposalCompletion is not null)
            {
                return _disposalCompletion.Task;
            }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposalCompletion = completion;
            _disposed = true;
        }
        try
        {
            PauseObservingChanges();
        }
        catch
        {
            // Cancellation callbacks cannot prevent the remaining producers from being drained.
        }
        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // Cancellation callbacks cannot prevent the remaining producers from being drained.
        }
        SetConnectionState(AgentRuntimeConnectionState.Disposed);
        Task[] retainedTasks;
        lock (_producerTaskLock)
        {
            retainedTasks = _retainedProducerTasks.ToArray();
        }
        _ = CompleteDisposalAsync(retainedTasks, completion);
        return completion.Task;
    }

    private async Task CompleteDisposalAsync(
        Task[] retainedTasks,
        TaskCompletionSource completion)
    {
        try
        {
            await Task.WhenAll(retainedTasks).ConfigureAwait(false);
        }
        catch
        {
            foreach (var task in retainedTasks)
            {
                if (task.IsFaulted)
                {
                    _ = task.Exception;
                }
            }
        }
        lock (_producerTaskLock)
        {
            _retainedProducerTasks.Clear();
        }
        _resnapshotGate.Dispose();
        _lifetime.Dispose();
        completion.TrySetResult();
    }
}

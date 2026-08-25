namespace Sunder.Package.Agent.TestSupport;

public sealed class AsyncGate
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public bool IsReleased => _released.Task.IsCompleted;

    public async Task EnterAndWaitAsync(CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SignalEntered() => _entered.TrySetResult();

    public void Release() => _released.TrySetResult();
}

public sealed class AsyncCheckpoint
{
    private readonly object _syncRoot = new();
    private readonly List<Waiter> _waiters = [];
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Signal()
    {
        List<TaskCompletionSource> completed;
        lock (_syncRoot)
        {
            Interlocked.Increment(ref _count);
            completed = _waiters
                .Where(waiter => waiter.ExpectedCount <= _count)
                .Select(static waiter => waiter.Completion)
                .ToList();
            _waiters.RemoveAll(waiter => waiter.ExpectedCount <= _count);
        }

        foreach (var waiter in completed)
        {
            waiter.TrySetResult();
        }
    }

    public Task WaitForCountAsync(int expectedCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);
        lock (_syncRoot)
        {
            if (_count >= expectedCount)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new Waiter(expectedCount, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record Waiter(int ExpectedCount, TaskCompletionSource Completion);
}

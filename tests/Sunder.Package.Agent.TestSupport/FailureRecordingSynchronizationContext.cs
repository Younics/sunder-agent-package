using System.Collections.Concurrent;

namespace Sunder.Package.Agent.TestSupport;

/// <summary>Records failures posted by fire-and-forget presentation work.</summary>
public sealed class FailureRecordingSynchronizationContext : SynchronizationContext
{
    private int _postCount;

    public ConcurrentQueue<Exception> Exceptions { get; } = [];
    public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int PostCount => Volatile.Read(ref _postCount);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref _postCount);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { callback(state); }
            catch (Exception exception)
            {
                Exceptions.Enqueue(exception);
                Failure.TrySetResult(exception);
            }
        });
    }
}

extern alias SubagentsPackage;

using OwnerStreamObserver = SubagentsPackage::Sunder.Package.Agent.Shared.Presentation.OwnerStreamObserver;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class OwnerStreamObserverTests
{
    [Fact]
    public void RetryDelay_IsBoundedExponentialAndJittered()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), OwnerStreamObserver.CalculateRetryDelay(0, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(300), OwnerStreamObserver.CalculateRetryDelay(0, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), OwnerStreamObserver.CalculateRetryDelay(1, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(4), OwnerStreamObserver.CalculateRetryDelay(4, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(4800), OwnerStreamObserver.CalculateRetryDelay(100, 1));
    }

    [Fact]
    public async Task RunAsync_ReportsAvailabilityRetriesAndShutdown()
    {
        var retry = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var availability = new List<bool>();
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        var observer = new OwnerStreamObserver(
            (_, delay) => retry.TrySetResult(delay),
            static (_, _) => Task.CompletedTask,
            static () => 0.5);
        observer.AvailabilityChanged += static _ => throw new InvalidOperationException(
            "Injected availability listener failure.");
        observer.AvailabilityChanged += availability.Add;

        var observation = observer.RunAsync(async (markAvailable, token) =>
        {
            markAvailable();
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("Injected provider retirement.");
            }

            secondConnection.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token);

        Assert.Equal(TimeSpan.FromMilliseconds(250), await retry.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await secondConnection.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(observer.IsAvailable);

        cancellation.Cancel();
        await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(observer.IsAvailable);
        Assert.Equal([true, false, true, false], availability);
    }

    [Fact]
    public async Task RunAsync_DoesNotConvertFatalFailureIntoRetry()
    {
        var retryCount = 0;
        var observer = new OwnerStreamObserver(
            (_, _) => retryCount++,
            static (_, _) => Task.CompletedTask,
            static () => 0.5);

        await Assert.ThrowsAsync<OutOfMemoryException>(() => observer.RunAsync(
            static (_, _) => Task.FromException(new OutOfMemoryException("Injected fatal failure.")),
            CancellationToken.None));

        Assert.Equal(0, retryCount);
        Assert.False(observer.IsAvailable);
    }
}

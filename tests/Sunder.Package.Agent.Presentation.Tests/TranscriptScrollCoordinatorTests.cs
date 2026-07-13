using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class TranscriptScrollCoordinatorTests
{
    [AvaloniaFact]
    public async Task Dispose_CancelsStartedPagingAndSuppressesPostLoadMutation()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCompleted = false;
        var failureCount = 0;
        var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }

                loadCompleted = true;
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            pagingFailed: _ => failureCount++);

        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Dispose();
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancellationObserved.Task.IsCompletedSuccessfully);
        Assert.False(loadCompleted);
        Assert.Equal(0, failureCount);
    }

    [AvaloniaFact]
    public async Task PagingFailure_IsReportedThroughExplicitCallback()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reportedFailure = null;
        using var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => true,
            loadNewerRowsAsync: async (_, _) =>
            {
                loadStarted.TrySetResult();
                await releaseFailure.Task;
                throw new InvalidOperationException("Injected paging failure.");
            },
            hasNewerRows: () => true,
            pagingFailed: exception => reportedFailure = exception);

        Assert.True(coordinator.QueueLoadNewerRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFailure.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = Assert.IsType<InvalidOperationException>(reportedFailure);
        Assert.Equal("Injected paging failure.", failure.Message);
    }

    [AvaloniaFact]
    public async Task PagingFailureCallbackFailure_DoesNotFaultTrackedLifetimeOperation()
    {
        using var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: (_, _) => Task.FromException<bool>(
                new InvalidOperationException("Injected load failure.")),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            pagingFailed: _ => throw new InvalidOperationException("Injected callback failure."));

        Assert.True(coordinator.QueueLoadOlderRows());

        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

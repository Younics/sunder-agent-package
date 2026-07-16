using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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

    [Fact]
    public void OlderPaging_DoesNotRestoreAnchorAfterUserScroll()
    {
        Assert.True(TranscriptScrollCoordinator.ShouldRestoreOlderRowsAnchor(100, 100));
        Assert.False(TranscriptScrollCoordinator.ShouldRestoreOlderRowsAnchor(100, 101));
    }

    [AvaloniaFact]
    public async Task OlderPaging_DoesNotOverrideUserScrollWhileLoadIsBlocked()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1000 },
        };
        var window = new Window { Width = 200, Height = 200, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false);

        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        window.MouseMove(new Point(100, 100), RawInputModifiers.None);
        window.MouseWheel(new Point(100, 100), new Vector(0, -2), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;
        Assert.True(userOffset > 0);
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(userOffset, scrollViewer.Offset.Y);
        window.Close();
    }

    [Fact]
    public void AnchorRestoration_UsesCurrentOffsetAfterLayoutAnchoring()
    {
        Assert.Equal(
            530,
            TranscriptScrollCoordinator.CalculateRestoredOffset(
                currentOffset: 480,
                currentTop: 20,
                capturedTop: -30));
    }

    [AvaloniaFact]
    public async Task QueuedBottomWrite_DoesNotOverrideLaterWheelInput()
    {
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1400 },
        };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 600);
        using var coordinator = CreateCoordinator(scrollViewer);

        coordinator.QueueScrollToBottom();
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 2), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;

        await coordinator.PendingPagingOperations;

        Assert.Equal(userOffset, scrollViewer.Offset.Y);
        Assert.True(scrollViewer.Offset.Y < scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ExplicitBottomWrite_OverridesEarlierUserInput()
    {
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1400 },
        };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 600);
        using var coordinator = CreateCoordinator(scrollViewer);
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 2), RawInputModifiers.None);

        coordinator.QueueScrollToBottom(force: true);
        await coordinator.PendingPagingOperations;

        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DownwardInputAtBottom_PreservesTailFollowing()
    {
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = CreateCoordinator(scrollViewer);

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, -2), RawInputModifiers.None);
        content.Height = 1500;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await coordinator.PendingPagingOperations;

        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewportExpansion_RestoresVisibleAnchor()
    {
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = CreateCoordinator(scrollViewer, rows);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(210, scrollViewer.Offset.Y, precision: 3);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;

        coordinator.BeginViewportMutation();
        first.Height = 280;
        coordinator.OnViewportContentChanged();
        await coordinator.PendingPagingOperations;

        var restoredTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTop, restoredTop, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewportRestoration_DoesNotOverrideLaterWheelInput()
    {
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = CreateCoordinator(scrollViewer, rows);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(210, scrollViewer.Offset.Y, precision: 3);

        coordinator.BeginViewportMutation();
        first.Height = 280;
        coordinator.OnViewportContentChanged();
        window.MouseMove(new Point(130, 110), RawInputModifiers.None);
        window.MouseWheel(new Point(130, 110), new Vector(0, 2), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;

        await coordinator.PendingPagingOperations;

        Assert.Equal(userOffset, scrollViewer.Offset.Y);
        window.Close();
    }

    private static TranscriptScrollCoordinator CreateCoordinator(
        ScrollViewer scrollViewer,
        Control? itemsControl = null)
        => new(
            scrollViewer,
            itemsControl,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false);

    private static TranscriptRowPresenter CreateAnchorRow(string key, double height)
        => new()
        {
            AnchorKey = key,
            Height = height,
            Content = new Border(),
        };
}

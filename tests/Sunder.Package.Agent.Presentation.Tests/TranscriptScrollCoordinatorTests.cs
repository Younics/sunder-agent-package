using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class TranscriptScrollCoordinatorTests
{
    [Fact]
    public void RunActivityState_QuietExpiryWhileDetachedShowsAfterResume()
    {
        var isFollowingLatest = false;
        using var state = new AgentRunActivityState(
            isRunActive: () => true,
            isFollowingLatest: () => isFollowingLatest,
            quietDelay: TimeSpan.Zero);
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [
                new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    "response",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null),
            ],
            now,
            now);

        state.TrackTurn(turn, scheduleQuietTimer: true);
        Assert.False(state.ShouldShow);

        isFollowingLatest = true;
        state.NotifyFollowStateChanged();

        Assert.True(state.ShouldShow);
    }

    [AvaloniaFact]
    public async Task AnchorHost_ForwardsOnlyTailWhileFollowingAndRowsWhileReading()
    {
        var row = CreateHostedAnchorRow("row", 600);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Same(tail, scrollViewer.CurrentAnchor);

        host.SetFollowingTail(false);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Same(row, scrollViewer.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnchorHost_SuspensionIsNestedIdempotentAndReconcilesLatestMode()
    {
        var row = CreateHostedAnchorRow("row", 600);
        var activity = CreateHostedAnchorRow("activity", 40, TranscriptAnchorItemRole.Persistent);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        var host = new TranscriptScrollAnchorHost { Children = { row, activity, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(activity);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var first = host.SuspendAnchoring();
        var second = host.SuspendAnchoring();
        host.SetFollowingTail(false);
        Assert.True(host.IsAnchoringSuspended);
        Assert.Null(provider.CurrentAnchor);

        first.Dispose();
        first.Dispose();
        Assert.True(host.IsAnchoringSuspended);
        Assert.Null(provider.CurrentAnchor);

        second.Dispose();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(host.IsAnchoringSuspended);
        Assert.Same(row, provider.CurrentAnchor);
        Assert.NotSame(activity, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnchorHost_ReconcilesRecycledPresenterRole()
    {
        var candidate = CreateHostedAnchorRow("candidate", 600);
        var host = new TranscriptScrollAnchorHost { Children = { candidate } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(candidate);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        host.SetFollowingTail(false);
        Assert.Same(candidate, provider.CurrentAnchor);

        candidate.AnchorRole = TranscriptAnchorItemRole.Persistent;
        Assert.Null(provider.CurrentAnchor);
        candidate.AnchorRole = TranscriptAnchorItemRole.TailSentinel;
        host.SetFollowingTail(true);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Same(candidate, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewportMutation_UserInputReleasesSuspensionAndForwardsTransientRows()
    {
        var row = CreateHostedAnchorRow("row", 900);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = CreateCoordinator(scrollViewer, host, host, isFollowingLatest: false);

        coordinator.BeginViewportMutation();
        Assert.True(host.IsAnchoringSuspended);
        Assert.Null(provider.CurrentAnchor);
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 1), RawInputModifiers.None);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.False(host.IsAnchoringSuspended);
        Assert.Same(row, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ViewportMutation_DeactivationAndDisposeReleaseSuspensionInTailMode()
    {
        var row = CreateHostedAnchorRow("row", 900);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var coordinator = CreateCoordinator(scrollViewer, host, host);

        coordinator.BeginViewportMutation();
        Assert.False(host.IsAnchoringSuspended);
        Assert.Same(tail, provider.CurrentAnchor);
        coordinator.SetPresentationActive(false);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(host.IsAnchoringSuspended);
        Assert.Same(tail, provider.CurrentAnchor);

        coordinator.SetPresentationActive(true);
        coordinator.BeginViewportMutation();
        Assert.False(host.IsAnchoringSuspended);
        coordinator.Dispose();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(host.IsAnchoringSuspended);
        Assert.Same(tail, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PagingCancellation_ReleasesSuspensionAndForwardsTransientRows()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = CreateHostedAnchorRow("row", 900);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            host,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            anchorHost: host);

        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(host.IsAnchoringSuspended);
        coordinator.SetPresentationActive(false);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(host.IsAnchoringSuspended);
        Assert.Same(row, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task KeyboardFocusAuthorityCancelsPendingPageBeforeItCanMutateRows()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var button = new Button { Content = "Focus target", Margin = new Thickness(0, 600, 0, 0) };
        var scrollViewer = new ScrollViewer { Content = button };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false);

        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task;
        Assert.True(button.Focus(NavigationMethod.Tab));
        await cancellationObserved.Task;
        await coordinator.PendingPagingOperations;

        Assert.False(Assert.IsType<bool>(coordinator.GetType()
            .GetField(
                "_loadOlderPending",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnchorHost_UnregistersRecycledRowsAndReattachesTailCandidate()
    {
        var row = CreateHostedAnchorRow("row", 600);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        host.SetFollowingTail(false);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Same(row, provider.CurrentAnchor);

        provider.UnregisterAnchorCandidate(row);
        Assert.Null(provider.CurrentAnchor);
        host.SetFollowingTail(true);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Same(tail, provider.CurrentAnchor);

        window.Content = null;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        window.Content = scrollViewer;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Same(tail, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedOlderPageUsesRowAnchorsDuringMutationThenRestoresTailMode()
    {
        var row = CreateHostedAnchorRow("row", 600);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var usedTailDuringMutation = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: (_, _) =>
            {
                usedTailDuringMutation = ReferenceEquals(provider.CurrentAnchor, tail);
                return Task.FromResult(false);
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            anchorHost: host);

        Assert.True(coordinator.QueueLoadOlderRows());
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.False(usedTailDuringMutation);
        Assert.Same(tail, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnchorHost_TailCandidateKeepsCompletedLayoutsAtBottomDuringGrowth()
    {
        var content = CreateHostedAnchorRow("content", 600);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { content, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(content);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Same(tail, scrollViewer.CurrentAnchor);
        var initialTailTop = Assert.IsType<Point>(tail.TranslatePoint(default, scrollViewer)).Y;

        content.Height = 900;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var finalTailTop = Assert.IsType<Point>(tail.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTailTop, finalTailTop, precision: 3);
        Assert.InRange(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y,
            -1,
            1);
        window.Close();
    }

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
    public async Task Dispose_CancelsQueuedPagingBeforeDispatcherCallback()
    {
        var loadInvoked = false;
        var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: (_, _) =>
            {
                loadInvoked = true;
                return Task.FromResult(false);
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false);

        Assert.True(coordinator.QueueLoadOlderRows());
        coordinator.Dispose();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(loadInvoked);
        Assert.False(Assert.IsType<bool>(coordinator.GetType()
            .GetField(
                "_loadOlderPending",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)));
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

    [AvaloniaFact]
    public async Task UpwardInputAtClampedTopQueuesOlderPage()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1000 },
        };
        var window = new Window { Width = 200, Height = 200, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(0, scrollViewer.Offset.Y);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: (_, _) =>
            {
                loadStarted.TrySetResult();
                return Task.FromResult(false);
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false);

        window.MouseMove(new Point(100, 100), RawInputModifiers.None);
        window.MouseWheel(new Point(100, 100), new Vector(0, 2), RawInputModifiers.None);

        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, scrollViewer.Offset.Y);
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
    public async Task AuthoritativeInitialAnchorSupersedesQueuedPreliminaryToolAnchor()
    {
        var executionId = Guid.NewGuid();
        var authoritativeKey = new TranscriptRowAnchorKey($"tool-execution:{executionId:N}");
        var authoritativeRow = CreateAnchorRow(authoritativeKey.ToString(), 100);
        authoritativeRow.AnchorKey = authoritativeKey;
        var rows = new StackPanel
        {
            Children =
            {
                new Border { Height = 700 },
                authoritativeRow,
                new Border { Height = 300 },
            },
        };
        var scrollViewer = new ScrollViewer { Content = rows };
        var jumpButton = new Button();
        var owner = new Grid { Children = { scrollViewer, jumpButton } };
        TranscriptViewportAnchorData? placedAnchor = null;
        using var behavior = new TranscriptViewBehavior(
            owner,
            scrollViewer,
            rows,
            jumpButton,
            canLoadOlder: () => false,
            loadOlder: (_, _) => Task.FromResult(false),
            canLoadNewer: () => false,
            loadNewer: (_, _) => Task.FromResult(false),
            hasNewer: () => false,
            isFollowingLatest: () => false,
            isInitialLoading: () => false,
            hasRows: () => true,
            hasTranscriptSelection: () => true,
            detachFromLatest: () => false,
            reachedLatest: () => false,
            viewportAnchorChanged: anchor => placedAnchor = anchor,
            enumerateRealizedAnchors: () => rows.Children
                .OfType<TranscriptRowPresenter>()
                .Select(row => (row.AnchorKey!, (Control)row)),
            realizeAnchorVisual: anchorKey => rows.Children
                .OfType<TranscriptRowPresenter>()
                .FirstOrDefault(row => Equals(row.AnchorKey, anchorKey)));
        behavior.MarkInitialPlacementPending(initialAnchorKey: new TranscriptRowAnchorKey("tool:call-1"));
        var authoritativePlacementQueued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Loaded += (_, _) =>
        {
            behavior.SetInitialPlacementAnchor(authoritativeKey);
            authoritativePlacementQueued.TrySetResult();
        };
        var window = new Window { Width = 320, Height = 240, Content = owner };

        window.Show();
        await authoritativePlacementQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await behavior.WaitForInitialPresentationAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

        Assert.Equal(authoritativeKey, placedAnchor?.AnchorKey);
        Assert.Equal(1, scrollViewer.Opacity);
        Assert.True(scrollViewer.Offset.Y > 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PendingMarkdownKeepsAnchorUntilDeferredRenderCompletes()
    {
        var builder = new ObservableStringBuilder("Initial response.");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var row = new TranscriptRowPresenter
        {
            AnchorKey = "row",
            Content = presenter,
        };
        var items = new StackPanel
        {
            Children =
            {
                new Border { Height = 600 },
                row,
                new Border { Height = 600 },
            },
        };
        var scrollViewer = new ScrollViewer { Content = items };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        window.Show();
        for (var attempt = 0; attempt < 100 && renderedCount == 0; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(1);
        }
        Assert.True(renderedCount > 0);
        var renderer = Assert.Single(presenter.Children.OfType<StableMarkdownRenderer>());
        scrollViewer.Offset = new Vector(0, 500);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            items,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false);
        presenter.Rendered += (_, _) => coordinator.OnRenderedContentChanged();
        renderer.SelectAll();

        coordinator.BeginViewportMutation();
        builder.Append("\n\n" + string.Join(
            "\n\n",
            Enumerable.Repeat("## Deferred heading\n\nDeferred body.", 40)));
        coordinator.OnViewportContentChanged();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(
            TranscriptViewportMutationStatus.Active,
            coordinator.DiagnosticSnapshot.Mutation?.Status);

        renderer.ClearSelection();
        for (var attempt = 0;
             attempt < 200 && coordinator.DiagnosticSnapshot.Mutation?.Status
                 == TranscriptViewportMutationStatus.Active;
             attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        }
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            TranscriptViewportMutationStatus.Completed,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        Assert.False(presenter.IsRenderPending);
        await presenter.PendingRenderOperations;
        window.Close();
    }

    [AvaloniaFact]
    public async Task FollowingTailRenderPassCapReleasesAnchorSuspensionAndTrailingCompensator()
    {
        var builder = new ObservableStringBuilder("Initial response.");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var row = CreateHostedAnchorRow("row", 220);
        row.Content = presenter;
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        var host = new TranscriptScrollAnchorHost
        {
            Children =
            {
                new Border { Height = 400 },
                row,
                tail,
            },
        };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        window.Show();
        for (var attempt = 0; attempt < 100 && !presenter.Children.OfType<StableMarkdownRenderer>().Any(); attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(1);
        }
        var renderer = Assert.Single(presenter.Children.OfType<StableMarkdownRenderer>());
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var initialMinHeight = host.MinHeight;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            host,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            anchorHost: host,
            realizeTailVisual: () => tail);
        presenter.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(presenter);
        renderer.SelectAll();

        var writesBeforeMutation = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
        coordinator.BeginViewportMutation();
        builder.Append("\n\n" + string.Join(
            "\n\n",
            Enumerable.Repeat("## Deferred heading\n\nDeferred body.", 40)));
        coordinator.OnViewportContentChanged();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.True(presenter.IsRenderPending);
        Assert.False(host.IsAnchoringSuspended);
        Assert.Equal(initialMinHeight, host.MinHeight);
        Assert.Equal(writesBeforeMutation, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(
            TranscriptViewportMutationMode.FollowingNativeObserver,
            coordinator.DiagnosticSnapshot.Mutation?.Mode);

        renderer.ClearSelection();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            TranscriptViewportMutationStatus.Completed,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        Assert.Equal(writesBeforeMutation, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        window.Close();
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
    public async Task ForcedBottomRequest_IsNotDowngradedByLaterPassiveRequest()
    {
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1400 },
        };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 400);
        var isFollowingLatest = false;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        coordinator.QueueScrollToBottom(force: true);
        coordinator.QueueScrollToBottom();
        await coordinator.PendingPagingOperations;

        Assert.True(isFollowingLatest);
        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ExplicitBottomRequest_WinsOverLaterPersistedAnchorRestore()
    {
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1400 },
        };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 500);
        using var coordinator = CreateCoordinator(scrollViewer);

        coordinator.QueueScrollToBottom(force: true);
        coordinator.RestoreViewportAnchor(new TranscriptViewportAnchorData(
            AnchorKey: null,
            OffsetY: 120,
            DistanceFromBottom: 1040));
        await coordinator.PendingPagingOperations;

        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ContinuousWheelBurst_IsNotReversedByRenderedContentChange()
    {
        var content = new Border { Height = 1800 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        for (var index = 0; index < 12; index++)
        {
            window.MouseWheel(
                new Point(120, 120),
                new Vector(0, 0.2),
                RawInputModifiers.None);
        }
        var userOffset = scrollViewer.Offset.Y;

        coordinator.OnRenderedContentChanged();
        content.Height += 100;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await coordinator.PendingPagingOperations;

        Assert.False(isFollowingLatest);
        Assert.Equal(userOffset, scrollViewer.Offset.Y, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DetachmentMutationDoesNotConsumeUpwardWheelInput()
    {
        var content = new Border { Height = 1800 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var initialOffset = scrollViewer.Offset.Y;
        var isFollowingLatest = true;
        TranscriptScrollCoordinator? coordinator = null;
        coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                var activeCoordinator = coordinator!;
                activeCoordinator.BeginTranscriptMutation();
                content.Height -= 100;
                activeCoordinator.OnTranscriptChanged();
                return true;
            });
        using (coordinator)
        {
            window.MouseMove(new Point(120, 120), RawInputModifiers.None);
            window.MouseWheel(new Point(120, 120), new Vector(0, 1), RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var userOffset = scrollViewer.Offset.Y;

            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(isFollowingLatest);
            Assert.True(userOffset < initialOffset);
            Assert.Equal(userOffset, scrollViewer.Offset.Y, precision: 3);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task NestedAndHorizontalScrollGesturesDoNotInvalidateOuterIntent()
    {
        var nestedContent = new Border { Height = 900 };
        var nestedViewer = new ScrollViewer
        {
            Height = 200,
            Content = nestedContent,
        };
        var outerGestureTarget = new Border { Height = 400 };
        var outerViewer = new ScrollViewer
        {
            Content = new StackPanel
            {
                Children =
                {
                    new Border { Height = 400 },
                    nestedViewer,
                    outerGestureTarget,
                },
            },
        };
        var window = new Window { Width = 320, Height = 300, Content = outerViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        nestedViewer.Offset = new Vector(0, 200);
        using var coordinator = CreateCoordinator(outerViewer);
        var interactionRevisionField = coordinator.GetType().GetField(
            "_interactionRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var gestureHandler = coordinator.GetType().GetMethod(
            "OnUserScrollGesture",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var initialRevision = Assert.IsType<long>(interactionRevisionField.GetValue(coordinator));

        gestureHandler.Invoke(coordinator, [outerViewer, new ScrollGestureEventArgs(1, new Vector(0, -10))
        {
            RoutedEvent = InputElement.ScrollGestureEvent,
            Source = nestedContent,
        }]);
        gestureHandler.Invoke(coordinator, [outerViewer, new ScrollGestureEventArgs(2, new Vector(10, 0))
        {
            RoutedEvent = InputElement.ScrollGestureEvent,
            Source = outerGestureTarget,
        }]);

        Assert.Equal(
            initialRevision,
            Assert.IsType<long>(interactionRevisionField.GetValue(coordinator)));

        gestureHandler.Invoke(coordinator, [outerViewer, new ScrollGestureEventArgs(3, new Vector(0, -10))
        {
            RoutedEvent = InputElement.ScrollGestureEvent,
            Source = outerGestureTarget,
        }]);

        Assert.Equal(
            initialRevision + 1,
            Assert.IsType<long>(interactionRevisionField.GetValue(coordinator)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task FractionalUpwardInput_DetachesInsideBottomThreshold()
    {
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var detachedCount = 0;
        var reachedLatestCount = 0;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            onDetachedFromLatest: () =>
            {
                detachedCount++;
                return true;
            },
            onReachedLatest: () =>
            {
                reachedLatestCount++;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var detachedOffset = scrollViewer.Offset.Y;
        var distanceFromBottom = scrollViewer.Extent.Height
                                 - scrollViewer.Viewport.Height
                                 - detachedOffset;
        Assert.InRange(distanceFromBottom, 0.001, 23.999);

        content.Height = 1500;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await coordinator.PendingPagingOperations;

        Assert.Equal(detachedOffset, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(1, detachedCount);
        Assert.Equal(0, reachedLatestCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ManualReturnToTrueBottom_ResumesFollowing()
    {
        var scrollViewer = new ScrollViewer { Content = new Border { Height = 1400 } };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            },
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        Assert.False(isFollowingLatest);

        window.MouseWheel(new Point(120, 120), new Vector(0, -0.1), RawInputModifiers.None);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.True(isFollowingLatest);
        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ManualReturnToTrueBottom_FollowsRowAddedByReachedLatestCallback()
    {
        var first = CreateHostedAnchorRow("first", 700);
        var second = CreateHostedAnchorRow("second", 700);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { first, second, tail } };
        var host = new TranscriptScrollAnchorHost { Children = { rows } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(first);
        provider.RegisterAnchorCandidate(second);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        TranscriptScrollCoordinator? coordinator = null;
        coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            },
             onReachedLatest: () =>
             {
                 isFollowingLatest = true;
                 coordinator!.BeginTranscriptMutation();
                 var activity = CreateHostedAnchorRow("activity", 180);
                 rows.Children.Insert(rows.Children.Count - 1, activity);
                 provider.RegisterAnchorCandidate(activity);
                 coordinator.OnTranscriptChanged();
                 return true;
             },
             anchorHost: host);
        using (coordinator)
        {
            window.MouseMove(new Point(120, 120), RawInputModifiers.None);
            window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
            Assert.False(isFollowingLatest);

            window.MouseWheel(new Point(120, 120), new Vector(0, -0.1), RawInputModifiers.None);
            await coordinator.PendingPagingOperations;

            Assert.True(isFollowingLatest);
            Assert.Equal(
                scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
                scrollViewer.Offset.Y,
                precision: 3);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task NestedScrollViewerInput_DoesNotDetachOuterTranscript()
    {
        var nestedViewer = new ScrollViewer
        {
            Height = 120,
            Content = new Border { Height = 600 },
        };
        var outerViewer = new ScrollViewer { Content = nestedViewer };
        var window = new Window { Width = 240, Height = 240, Content = outerViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        nestedViewer.Offset = new Vector(0, 200);
        Assert.True(TranscriptScrollCoordinator.CanScrollViewerConsume(nestedViewer, 1));
        Assert.True(TranscriptScrollCoordinator.CanScrollViewerConsume(nestedViewer, -1));
        var detachedCount = 0;
        using var coordinator = new TranscriptScrollCoordinator(
            outerViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            onDetachedFromLatest: () =>
            {
                detachedCount++;
                return true;
            });

        window.MouseMove(new Point(120, 60), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 60), new Vector(0, 1), RawInputModifiers.None);

        Assert.Equal(0, detachedCount);
        Assert.True(nestedViewer.Offset.Y < 200);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LaterWheelInput_CancelsQueuedExplicitBottomWrite()
    {
        var scrollViewer = new ScrollViewer { Content = new Border { Height = 1400 } };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 600);
        using var coordinator = CreateCoordinator(scrollViewer);

        coordinator.QueueScrollToBottom(force: true);
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 2), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;
        await coordinator.PendingPagingOperations;

        Assert.Equal(userOffset, scrollViewer.Offset.Y);
        Assert.True(scrollViewer.Offset.Y < scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NewerPagingCompletion_DoesNotOverrideLaterUpwardInput()
    {
        var hasNewerRows = true;
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => true,
            loadNewerRowsAsync: (_, _) => Task.FromResult(true),
            hasNewerRows: () => hasNewerRows);

        Assert.True(coordinator.QueueLoadNewerRows());
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        var detachedOffset = scrollViewer.Offset.Y;
        hasNewerRows = false;
        content.Height = 1500;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(detachedOffset, scrollViewer.Offset.Y, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TailIntentDuringBlockedNewerPageContinuesUntilLatest()
    {
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasNewerRows = true;
        var isFollowingLatest = false;
        var loadCount = 0;
        TranscriptPageAnchorAuthority? pageAuthority = null;
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => hasNewerRows,
            loadNewerRowsAsync: async (protectedAnchor, cancellationToken) =>
            {
                pageAuthority = Assert.IsType<TranscriptPageAnchorAuthority>(protectedAnchor);
                loadCount++;
                if (loadCount == 1)
                {
                    firstLoadStarted.TrySetResult();
                    await releaseFirstLoad.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    hasNewerRows = false;
                }
                return true;
            },
            hasNewerRows: () => hasNewerRows,
            isFollowingLatest: () => isFollowingLatest,
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });
        Assert.True(coordinator.QueueLoadNewerRows());
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, -1), RawInputModifiers.None);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        var coordinatorType = typeof(TranscriptScrollCoordinator);
        var interactionRevision = (long)coordinatorType.GetField(
            "_interactionRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(coordinator)!;
        var resumeRevision = (long)coordinatorType.GetField(
            "_loadNewerResumeInteractionRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(coordinator)!;
        Assert.Equal(interactionRevision, resumeRevision);
        Assert.NotNull(pageAuthority);
        Assert.False(pageAuthority.ResolveCurrentAnchor().PreserveAnchor);
        releaseFirstLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, loadCount);
        Assert.False(hasNewerRows);
        Assert.True(isFollowingLatest);
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
        var content = CreateHostedAnchorRow("content", 1400);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { content, tail } };
        var host = new TranscriptScrollAnchorHost { Children = { rows } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(content);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = CreateCoordinator(scrollViewer, rows, host);

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
        using var coordinator = CreateCoordinator(scrollViewer, rows, isFollowingLatest: false);
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
        using var coordinator = CreateCoordinator(scrollViewer, rows, isFollowingLatest: false);
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

    [AvaloniaFact]
    public async Task LayoutChangeAtBottom_DoesNotResumeDetachedFollowing()
    {
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            },
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        Assert.False(isFollowingLatest);

        content.Height = 200;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await coordinator.PendingPagingOperations;

        Assert.Equal(0, scrollViewer.Offset.Y);
        Assert.False(isFollowingLatest);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ForcedNextBottomWrite_DoesNotOverrideLaterWheelInput()
    {
        var scrollViewer = new ScrollViewer { Content = new Border { Height = 1400 } };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 600);
        using var coordinator = CreateCoordinator(scrollViewer);

        coordinator.ForceScrollToBottomOnNextTranscriptChanged();
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 2), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;
        coordinator.OnTranscriptChanged();
        await coordinator.PendingPagingOperations;

        Assert.Equal(userOffset, scrollViewer.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reactivation_RestoresPersistedViewportAnchor()
    {
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        TranscriptViewportAnchorData? savedAnchor = null;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            setViewportAnchor: anchor => savedAnchor = anchor);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;

        coordinator.SetPresentationActive(false);
        Assert.NotNull(savedAnchor);
        Assert.NotNull(savedAnchor.Value.AnchorViewportTop);
        first.Height = 280;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        coordinator.SetPresentationActive(true);
        coordinator.RestoreViewportAnchor(savedAnchor);
        await coordinator.PendingPagingOperations;

        var restoredTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTop, restoredTop, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeactivationDoesNotCompleteCanceledInitialPlacement()
    {
        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Height = 1000 },
        };
        var window = new Window { Width = 200, Height = 200, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = CreateCoordinator(scrollViewer);
        var completionCount = 0;

        coordinator.QueueScrollToBottomAfterLayoutSettles(() => completionCount++);
        coordinator.SetPresentationActive(false);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, completionCount);
        coordinator.SetPresentationActive(true);
        coordinator.QueueScrollToBottomAfterLayoutSettles(() => completionCount++);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, completionCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReactivationAnchor_SurvivesCompletionOfPreviouslyQueuedPage()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        TranscriptViewportAnchorData? savedAnchor = null;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            setViewportAnchor: anchor => savedAnchor = anchor);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.SetPresentationActive(false);
        Assert.NotNull(savedAnchor);
        coordinator.SetPresentationActive(true);
        coordinator.RestoreViewportAnchor(savedAnchor);
        coordinator.DiscardPendingTranscriptMutation();
        first.Height = 280;
        coordinator.OnTranscriptChanged();
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var restoredTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTop, restoredTop, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeactivationAfterPageMutation_PreservesPrePageViewportAnchor()
    {
        var allowMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var initialOffset = scrollViewer.Offset.Y;
        TranscriptViewportAnchorData? savedAnchor = null;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                await allowMutation.Task.WaitAsync(cancellationToken);
                first.Height = 280;
                mutationCompleted.TrySetResult();
                await allowReturn.Task.WaitAsync(cancellationToken);
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            setViewportAnchor: anchor => savedAnchor = anchor);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.True(coordinator.QueueLoadOlderRows());
        Assert.NotNull(savedAnchor);
        allowMutation.TrySetResult();
        await mutationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, initialOffset);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.NotEqual(initialTop, Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y);

        coordinator.SetPresentationActive(false);
        coordinator.SetPresentationActive(true);
        coordinator.RestoreViewportAnchor(savedAnchor);
        allowReturn.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var restoredTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTop, restoredTop, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnchorRestoration_RealizesRecycledAnchorBeforeMeasuring()
    {
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        var realizationCount = 0;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            realizeAnchorVisual: anchorKey =>
            {
                if (!Equals(anchorKey, "second"))
                {
                    return null;
                }

                realizationCount++;
                if (!rows.Children.Contains(second))
                {
                    rows.Children.Insert(1, second);
                }

                return second;
            });

        coordinator.BeginViewportMutation();
        rows.Children.Remove(second);
        first.Height = 280;
        coordinator.OnViewportContentChanged();
        await coordinator.PendingPagingOperations;

        Assert.Equal(1, realizationCount);
        Assert.Contains(second, rows.Children);
        Assert.Equal(
            initialTop,
            Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y,
            precision: 3);
        Assert.Equal(
            TranscriptViewportMutationStatus.Completed,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DownwardInputWhileAlreadyAtBottom_ResumesDetachedFollowing()
    {
        var scrollViewer = new ScrollViewer { Content = new Border { Height = 1400 } };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var maxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        scrollViewer.Offset = new Vector(0, maxOffset);
        var isFollowingLatest = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            },
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        Assert.False(isFollowingLatest);
        scrollViewer.Offset = new Vector(0, maxOffset);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(isFollowingLatest);

        window.MouseWheel(new Point(120, 120), new Vector(0, -1), RawInputModifiers.None);
        await coordinator.PendingPagingOperations;

        Assert.True(isFollowingLatest);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FocusBringIntoView_DoesNotResumeDetachedFollowing()
    {
        var first = new Button { Height = 100, Content = "first" };
        var last = new Button { Height = 100, Content = "last" };
        var rows = new StackPanel
        {
            Children =
            {
                first,
                new Border { Height = 1200 },
                last,
            },
        };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            },
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        Assert.False(isFollowingLatest);
        scrollViewer.Offset = new Vector(0, 300);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        last.Focus(NavigationMethod.Tab);
        await coordinator.PendingPagingOperations;

        Assert.True(scrollViewer.Offset.Y > 300);
        Assert.False(isFollowingLatest);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OffscreenKeyboardFocus_DetachesTailFollowing()
    {
        var first = new Button { Height = 100, Content = "first" };
        var last = new Button { Height = 100, Content = "last" };
        var rows = new StackPanel
        {
            Children =
            {
                first,
                new Border { Height = 1200 },
                last,
            },
        };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            });

        first.Focus(NavigationMethod.Tab);
        await coordinator.PendingPagingOperations;

        Assert.False(isFollowingLatest);
        Assert.True(scrollViewer.Offset.Y < scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ActiveSelectionOffsetChange_DetachesTailFollowingAndCapturesAnchor()
    {
        var text = new SelectableTextBlock
        {
            Text = string.Join('\n', Enumerable.Range(0, 200).Select(index => $"line {index}")),
        };
        var scrollViewer = new ScrollViewer { Content = text };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isFollowingLatest = true;
        TranscriptViewportAnchorData? savedAnchor = null;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onDetachedFromLatest: () =>
            {
                isFollowingLatest = false;
                return true;
            },
            setViewportAnchor: anchor => savedAnchor = anchor);
        text.SelectAll();
        Assert.NotEmpty(text.SelectedText);

        scrollViewer.Offset = new Vector(0, Math.Max(0, scrollViewer.Offset.Y - 100));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.False(isFollowingLatest);
        Assert.NotNull(savedAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task VisibleKeyboardFocus_DoesNotInvalidatePendingPageAnchor()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var visibleButton = new Button { Content = "visible" };
        second.Content = visibleButton;
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 180);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        visibleButton.Focus(NavigationMethod.Tab);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(initialTop, Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y, precision: 3);
        first.Height = 280;
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var restoredTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTop, restoredTop, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedExternalDetachment_DoesNotRearmCoordinatorTailFollowing()
    {
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var detachedCount = 0;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            onDetachedFromLatest: () =>
            {
                detachedCount++;
                return false;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, 0.1), RawInputModifiers.None);
        content.Height = 1500;
        coordinator.OnTranscriptChanged();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await coordinator.PendingPagingOperations;

        Assert.Equal(1, detachedCount);
        Assert.True(scrollViewer.Offset.Y < scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 0.1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedTailResumeRestoresTransientAnchorHostMode()
    {
        var row = CreateHostedAnchorRow("row", 900);
        var tail = CreateHostedAnchorRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        tail.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var host = new TranscriptScrollAnchorHost { Children = { row, tail } };
        var provider = (IScrollAnchorProvider)host;
        provider.RegisterAnchorCandidate(row);
        provider.RegisterAnchorCandidate(tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            onReachedLatest: () => false,
            anchorHost: host);

        coordinator.QueueScrollToBottom(force: true);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var isFollowingTail = Assert.IsType<bool>(coordinator.GetType()
            .GetProperty(
                "IsFollowingTail",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
        Assert.False(isFollowingTail);
        Assert.Same(row, provider.CurrentAnchor);
        Assert.NotSame(tail, provider.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettledPlacement_DoesNotResumeExternallyDetachedState()
    {
        var scrollViewer = new ScrollViewer { Content = new Border { Height = 1400 } };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 400);
        var reachedLatestCount = 0;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            onReachedLatest: () =>
            {
                reachedLatestCount++;
                return true;
            });
        var initialOffset = scrollViewer.Offset.Y;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.QueueScrollToBottomAfterLayoutSettles(() => completed.TrySetResult());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.PendingPagingOperations;

        Assert.Equal(initialOffset, scrollViewer.Offset.Y);
        Assert.Equal(0, reachedLatestCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TailIntentDuringNewerLoad_ResumesWhenCaughtUpAtBottom()
    {
        var hasNewerRows = true;
        var isFollowingLatest = false;
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => hasNewerRows,
            loadNewerRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return true;
            },
            hasNewerRows: () => hasNewerRows,
            isFollowingLatest: () => isFollowingLatest,
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        Assert.True(coordinator.QueueLoadNewerRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(0, -1), RawInputModifiers.None);
        hasNewerRows = false;
        content.Height = 1600;
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(isFollowingLatest);
        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        coordinator.Dispose();
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    [AvaloniaFact]
    public async Task TailIntent_LoadsEveryNewerPageBeforeResuming()
    {
        var remainingPages = 2;
        var isFollowingLatest = false;
        var loadCount = 0;
        var content = new Border { Height = 1400 };
        var scrollViewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => remainingPages > 0,
            loadNewerRowsAsync: (_, _) =>
            {
                loadCount++;
                remainingPages--;
                content.Height += 200;
                return Task.FromResult(true);
            },
            hasNewerRows: () => remainingPages > 0,
            isFollowingLatest: () => isFollowingLatest,
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        Assert.True(coordinator.QueueLoadNewerRows(resumeFollowingWhenCaughtUp: true));
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(2, loadCount);
        Assert.True(isFollowingLatest);
        Assert.Equal(
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y,
            precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HorizontalWheelInput_DoesNotResumeDetachedFollowing()
    {
        var isFollowingLatest = false;
        var scrollViewer = new ScrollViewer { Content = new Border { Height = 1400 } };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });

        window.MouseMove(new Point(120, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(120, 120), new Vector(1, 0), RawInputModifiers.None);
        await coordinator.PendingPagingOperations;

        Assert.False(isFollowingLatest);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SpaceOnTranscriptButton_DoesNotResumeDetachedFollowing()
    {
        var button = new Button { Height = 100, Content = "Copy" };
        var rows = new StackPanel
        {
            Children =
            {
                new Border { Height = 1300 },
                button,
            },
        };
        var isFollowingLatest = false;
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 240, Height = 240, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            onReachedLatest: () =>
            {
                isFollowingLatest = true;
                return true;
            });
        button.Focus(NavigationMethod.Tab);

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        await coordinator.PendingPagingOperations;

        Assert.False(isFollowingLatest);
        window.Close();
    }

    [AvaloniaFact]
    public void PresentationDeactivation_ClearsInProgressPointerState()
    {
        using var coordinator = CreateCoordinator(new ScrollViewer());
        var type = typeof(TranscriptScrollCoordinator);
        type.GetField("_userScrollPending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(coordinator, true);
        type.GetField("_scrollBarInteractionActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(coordinator, true);
        type.GetField("_touchScrollRecognized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(coordinator, true);
        type.GetField("_touchScrollStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(coordinator, new Point(1, 1));

        coordinator.SetPresentationActive(false);

        Assert.False((bool)type.GetField("_userScrollPending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!);
        Assert.False((bool)type.GetField("_scrollBarInteractionActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!);
        Assert.False((bool)type.GetField("_touchScrollRecognized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!);
        Assert.Null(type.GetField("_touchScrollStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
    }

    [AvaloniaFact]
    public async Task ReplacementMutation_PreservesAnchorWhileOlderPageIsPending()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateAnchorRow("first", 180);
        var second = CreateAnchorRow("second", 180);
        var third = CreateAnchorRow("third", 180);
        var rows = new StackPanel { Children = { first, second, third } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 260, Height = 220, Content = scrollViewer };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        scrollViewer.Offset = new Vector(0, 210);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var initialTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false);
        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.BeginTranscriptReplacementMutation();
        first.Height = 280;
        coordinator.OnTranscriptChanged();
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var restoredTop = Assert.IsType<Point>(second.TranslatePoint(default, scrollViewer)).Y;
        Assert.Equal(initialTop, restoredTop, precision: 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReplacementMutation_SuppressesSupersededPagingFailure()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureCount = 0;
        using var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, _) =>
            {
                loadStarted.TrySetResult();
                await releaseFailure.Task;
                throw new InvalidOperationException("Superseded failure.");
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            pagingFailed: _ => failureCount++);
        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.BeginTranscriptReplacementMutation();
        coordinator.OnTranscriptChanged();
        releaseFailure.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, failureCount);
    }

    [AvaloniaFact]
    public async Task ReplacementMutation_SupersededPageDoesNotMutateCoordinatorState()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, _) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task;
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false);
        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.BeginTranscriptReplacementMutation();
        coordinator.OnTranscriptChanged();
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var type = typeof(TranscriptScrollCoordinator);
        Assert.False((bool)type.GetField("_userDetached", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!);
    }

    [AvaloniaFact]
    public async Task LiveMutationWhilePageIsBlocked_RearmsSupersededPagingEdge()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new TranscriptScrollCoordinator(
            new ScrollViewer(),
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: async (_, _) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task;
                return true;
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false);
        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.BeginTranscriptMutation();
        coordinator.OnTranscriptChanged();
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var field = typeof(TranscriptScrollCoordinator).GetField(
            "_isOlderEdgeArmed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.True((bool)field.GetValue(coordinator)!);
    }

    [Fact]
    public void ViewportOrInteractionMutation_RearmsSupersededPagingEdges()
    {
        Assert.True(TranscriptScrollCoordinator.ShouldRearmPagingEdge(
            loaded: true,
            queuedInteractionRevision: 10,
            currentInteractionRevision: 11,
            queuedAuthorityRevision: 20,
            currentAuthorityRevision: 20));
        Assert.True(TranscriptScrollCoordinator.ShouldRearmPagingEdge(
            loaded: false,
            queuedInteractionRevision: 10,
            currentInteractionRevision: 10,
            queuedAuthorityRevision: 20,
            currentAuthorityRevision: 20));
        Assert.True(TranscriptScrollCoordinator.ShouldRearmPagingEdge(
            loaded: true,
            queuedInteractionRevision: 10,
            currentInteractionRevision: 10,
            queuedAuthorityRevision: 20,
            currentAuthorityRevision: 21));
        Assert.False(TranscriptScrollCoordinator.ShouldRearmPagingEdge(
            loaded: true,
            queuedInteractionRevision: 10,
            currentInteractionRevision: 10,
            queuedAuthorityRevision: 20,
            currentAuthorityRevision: 20));
    }

    private static TranscriptScrollCoordinator CreateCoordinator(
        ScrollViewer scrollViewer,
        Control? itemsControl = null,
        bool isFollowingLatest = true)
        => new(
            scrollViewer,
            itemsControl,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest);

    private static TranscriptScrollCoordinator CreateCoordinator(
        ScrollViewer scrollViewer,
        Control itemsControl,
        TranscriptScrollAnchorHost anchorHost,
        bool isFollowingLatest = true)
        => new(
            scrollViewer,
            itemsControl,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            anchorHost: anchorHost);

    private static TranscriptRowPresenter CreateAnchorRow(
        string key,
        double height,
        TranscriptAnchorItemRole role = TranscriptAnchorItemRole.Transient)
        => new()
        {
            AnchorKey = key,
            AnchorRole = role,
            Height = height,
            Content = new Border(),
        };

    private static TranscriptRowPresenter CreateHostedAnchorRow(
        string key,
        double height,
        TranscriptAnchorItemRole role = TranscriptAnchorItemRole.Transient)
    {
        var result = CreateAnchorRow(key, height, role);
        result.IsRepeaterHosted = true;
        return result;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;
using Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class TranscriptViewportMutationTests
{
    [AvaloniaFact]
    public async Task ToolExpansionPreparation_RecycledHeaderCannotCommitRow()
    {
        var row = new object();
        var header = new Button { DataContext = row };
        var details = new TranscriptToolDetailHost { DataContext = row };
        var scope = new TranscriptRowPresenter
        {
            AnchorKey = "tool",
            AnchorRole = TranscriptAnchorItemRole.Transient,
            DataContext = row,
            Content = new StackPanel { Children = { header, details } },
        };
        var window = new Window { Width = 320, Height = 240, Content = scope };
        window.Show();
        await LayoutAsync(window);

        Assert.True(TranscriptToolExpansionPreparation.IsCurrent(
            header,
            row,
            scope,
            details,
            scope.AnchorKey!,
            static () => true));

        header.DataContext = new object();

        Assert.False(TranscriptToolExpansionPreparation.IsCurrent(
            header,
            row,
            scope,
            details,
            scope.AnchorKey!,
            static () => true));
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.SystemIdle);
    }

    [AvaloniaFact]
    public async Task ToolExpansionPreparation_LayoutSignalsCannotCompleteBeforeDetailsCommit()
    {
        var tool = CreateRow("tool", 220);
        var after = CreateRow("after", 400);
        var rows = new StackPanel { Children = { tool, after } };
        var host = CreateAnchorHost(rows, tool, after);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 120);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: false);

        var generation = coordinator.BeginViewportMutationPreparation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey!,
            tool,
            isExpanding: true);
        Assert.NotEqual(0, generation);
        Assert.True(host.HasExactAnchorLease);

        for (var pass = 0; pass < 4; pass++)
        {
            coordinator.OnViewportContentChanged();
            await LayoutAsync(window);
        }

        var preparing = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);
        Assert.Equal(TranscriptViewportMutationStatus.Active, preparing.Status);
        Assert.Equal(0, preparing.Passes);
        Assert.True(coordinator.CommitViewportMutationPreparation(
            generation,
            () => tool.Height = 360));
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.Equal(
            TranscriptViewportMutationStatus.Completed,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ToolExpansion_MismatchedScopeDoesNotAcquireExactAnchorLease()
    {
        var tool = CreateRow("tool", 300);
        var other = CreateRow("other", 300);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { tool, other, tail } };
        var host = CreateAnchorHost(rows, tool, other, tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, MaxOffset(scrollViewer));
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: true);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            other,
            isExpanding: true);

        Assert.False(host.HasExactAnchorLease);
        Assert.False(coordinator.DiagnosticSnapshot.ExactAnchorLeaseActive);
        Assert.NotEqual(
            TranscriptViewportMutationMode.ToolExpansionNativeAnchor,
            coordinator.DiagnosticSnapshot.Mutation?.Mode);
        coordinator.Dispose();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.SystemIdle);
    }

    [AvaloniaFact]
    public async Task ExactAnchorLease_TracksKeyAndRoleAcrossPresenterRecycling()
    {
        var row = CreateRow("tool", 120);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { row, tail } };
        var host = CreateAnchorHost(rows, row, tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 100, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 20);
        await LayoutAsync(window);

        using var lease = host.AcquireExactAnchorLease("tool");
        await LayoutAsync(window);
        Assert.Same(row, scrollViewer.CurrentAnchor);

        row.AnchorKey = "recycled";
        await LayoutAsync(window);
        Assert.Null(scrollViewer.CurrentAnchor);
        Assert.Equal("tool", host.ExactAnchorKey);

        row.AnchorKey = "tool";
        row.AnchorRole = TranscriptAnchorItemRole.TailSentinel;
        await LayoutAsync(window);
        Assert.Null(scrollViewer.CurrentAnchor);

        row.AnchorRole = TranscriptAnchorItemRole.Transient;
        await LayoutAsync(window);
        Assert.Same(row, scrollViewer.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ExactAnchorLease_PrefersMarkedStableDescendant()
    {
        var header = new Border { Height = 20 };
        TranscriptRowPresenter.SetIsExactAnchorTarget(header, true);
        var row = CreateRow("tool", 120, header);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { row, tail } };
        var host = CreateAnchorHost(rows, row, tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 100, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);

        using var lease = host.AcquireExactAnchorLease("tool");
        await LayoutAsync(window);

        Assert.Same(header, scrollViewer.CurrentAnchor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ToolExpansion_UsesExactNativeAnchorAndRelinquishesTailThroughLateGeometry()
    {
        var source = new ControlledGeometrySource();
        var tool = CreateRow("tool", 700, source);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { tool, tail } };
        var host = CreateAnchorHost(rows, tool, tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        var anchorRealizations = 0;
        var tailRealizations = 0;
        var detached = false;
        var jumpVisible = false;
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, MaxOffset(scrollViewer));
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: true,
            realizeAnchor: _ =>
            {
                anchorRealizations++;
                return null;
            },
            realizeTail: () =>
            {
                tailRealizations++;
                return null;
            },
            onDetached: () =>
            {
                detached = true;
                return true;
            },
            setJumpVisible: value => jumpVisible = value);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);
        var baseline = coordinator.DiagnosticSnapshot;
        var headerTop = Top(tool, scrollViewer);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool,
            isExpanding: true);
        source.Request();
        tool.Height = 980;
        coordinator.OnViewportContentChanged();
        await LayoutAsync(window);

        Assert.False(host.IsAnchoringSuspended);
        Assert.True(host.HasExactAnchorLease);
        Assert.Equal(tool.AnchorKey, host.ExactAnchorKey);
        Assert.Same(tool, scrollViewer.CurrentAnchor);
        Assert.Equal(
            TranscriptViewportMutationMode.ToolExpansionNativeAnchor,
            coordinator.DiagnosticSnapshot.Mutation?.Mode);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - headerTop), 0, 0.1);

        tool.Height = 1040;
        source.AdvanceGeometry();
        await LayoutAsync(window);
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.Equal(baseline.ProgrammaticOffsetWrites, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(0, coordinator.DiagnosticSnapshot.Mutation?.Corrections);
        Assert.Equal(0, anchorRealizations);
        Assert.Equal(0, tailRealizations);
        Assert.Equal(0, rows.MinHeight);
        Assert.False(host.IsAnchoringSuspended);
        Assert.True(host.HasExactAnchorLease);
        Assert.True(detached);
        Assert.True(jumpVisible);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - headerTop), 0, 0.1);
        Assert.NotSame(tail, scrollViewer.CurrentAnchor);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool,
            isExpanding: false);
        source.Request();
        tool.Height = 700;
        coordinator.OnViewportContentChanged();
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.Equal(baseline.ProgrammaticOffsetWrites, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.False(host.IsTrailingCompensatorActive);
        Assert.False(host.HasExactAnchorLease);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - headerTop), 0, 0.1);
        var writesAfterCollapse = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
        source.AdvanceGeometry();
        source.Settle();
        await LayoutAsync(window);
        Assert.Equal(writesAfterCollapse, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ToolExpansion_CrossesScrollbarThresholdWithoutMovingHeader()
    {
        var source = new ControlledGeometrySource();
        var before = CreateRow("before", 40);
        var tool = CreateRow("tool", 120, source);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { before, tool, tail } };
        var host = CreateAnchorHost(rows, before, tool, tail);
        var scrollViewer = new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        var detached = false;
        window.Show();
        await LayoutAsync(window);
        Assert.Equal(0, MaxOffset(scrollViewer), precision: 3);
        var headerTop = Top(tool, scrollViewer);
        var tailTop = Top(tail, scrollViewer);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: true,
            onDetached: () => detached = true);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);
        var writes = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool,
            isExpanding: true);
        source.Request();
        tool.Height = 500;
        coordinator.OnViewportContentChanged();
        await LayoutAsync(window);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - headerTop), 0, 0.1);
        Assert.True(Top(tail, scrollViewer) >= tailTop - 0.1);
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.True(MaxOffset(scrollViewer) > 0);
        Assert.True(detached);
        Assert.Equal(writes, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool,
            isExpanding: false);
        source.Request();
        tool.Height = 120;
        coordinator.OnViewportContentChanged();
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.False(host.IsTrailingCompensatorActive);
        Assert.False(host.HasExactAnchorLease);
        Assert.Equal(0, MaxOffset(scrollViewer), precision: 3);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - headerTop), 0, 0.1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ManualScrollDuringExactToolExpansionCancelsLateGeometryAbsolutely()
    {
        var source = new ControlledGeometrySource();
        var before = CreateRow("before", 200);
        var tool = CreateRow("tool", 220, source);
        var after = CreateRow("after", 400);
        var rows = new StackPanel { Children = { before, tool, after } };
        var host = CreateAnchorHost(rows, before, tool, after);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 180);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: false);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool,
            isExpanding: true);
        source.Request();
        tool.Height = 400;
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Passes >= 1);
        Assert.True(host.HasExactAnchorLease);

        window.MouseMove(new Point(150, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(150, 120), new Vector(0, 1), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;
        var writesAfterCancellation = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;

        Assert.Equal(
            TranscriptViewportMutationStatus.CanceledByAuthority,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        Assert.False(host.HasExactAnchorLease);
        Assert.False(host.IsTrailingCompensatorActive);

        tool.Height = 460;
        source.AdvanceGeometry();
        source.Settle();
        coordinator.OnRenderedContentChanged(source);
        await LayoutAsync(window);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        Assert.Equal(userOffset, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(writesAfterCancellation, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(
            TranscriptViewportMutationStatus.CanceledByAuthority,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompletedMutation_RepeatedSignalsCannotWriteOrRearm()
    {
        var content = CreateRow("content", 700);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { content, tail } };
        var host = CreateAnchorHost(rows, content, tail);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, MaxOffset(scrollViewer));
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(scrollViewer, rows, host, isFollowingLatest: true);

        coordinator.BeginViewportMutation();
        content.Height = 820;
        coordinator.OnViewportContentChanged();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        var terminal = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);
        var writes = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;

        for (var index = 0; index < 20; index++)
        {
            coordinator.OnRenderedContentChanged();
        }
        content.Height = 860;
        await LayoutAsync(window);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        Assert.Equal(terminal, coordinator.DiagnosticSnapshot.Mutation);
        Assert.Equal(writes, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(TranscriptViewportMutationStatus.Completed, terminal.Status);
        AssertTailAnchored(scrollViewer, tail);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SilentPendingGeometry_WatchdogTerminalizesAndReevaluatesPagingOnce()
    {
        var timeProvider = new ManualTimerTimeProvider();
        var source = new ControlledGeometrySource();
        var protectedRow = CreateRow("protected", 220, source);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { protectedRow, last } };
        var host = CreateAnchorHost(rows, protectedRow, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        var olderLoadCount = 0;
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 40);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: false,
            viewportMutationTimeProvider: timeProvider,
            canLoadOlder: () => true,
            loadOlder: (_, _) =>
            {
                olderLoadCount++;
                return Task.FromResult(false);
            });

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            protectedRow.AnchorKey,
            protectedRow);
        source.Request();
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Passes == 1);

        Assert.False(host.IsAnchoringSuspended);
        Assert.True(host.HasExactAnchorLease);
        Assert.True(source.IsGeometryPending);
        Assert.Equal(1, timeProvider.TimerCount);
        Assert.Equal(TimeSpan.FromSeconds(2), timeProvider.LastDueTime);
        timeProvider.FireAll();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        var terminal = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);
        Assert.Equal(TranscriptViewportMutationStatus.BudgetExhausted, terminal.Status);
        Assert.Equal(0, host.AnchoringSuspensionDepth);
        Assert.False(host.HasExactAnchorLease);
        Assert.Equal(1, olderLoadCount);

        coordinator.OnRenderedContentChanged(source);
        coordinator.OnViewportContentChanged();
        await LayoutAsync(window);
        Assert.Equal(1, olderLoadCount);
        Assert.Equal(terminal, coordinator.DiagnosticSnapshot.Mutation);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DetachedStructuralMutation_RestoresOneRealizedRowOncePerGeometryStamp()
    {
        var source = new ControlledGeometrySource();
        var first = CreateRow("first", 300);
        var protectedRow = CreateRow("protected", 220, source);
        var last = CreateRow("last", 400);
        var rows = new StackPanel { Children = { first, protectedRow, last } };
        var host = CreateAnchorHost(rows, first, protectedRow, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        var realizations = 0;
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        var protectedTop = Top(protectedRow, scrollViewer);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: false,
            realizeAnchor: _ =>
            {
                realizations++;
                return null;
            });

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.StructuralLayout,
            protectedRow.AnchorKey,
            protectedRow);
        source.Request();
        first.Height = 420;
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Corrections == 1);

        for (var index = 0; index < 8; index++)
        {
            coordinator.OnRenderedContentChanged(source);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        Assert.Equal(1, coordinator.DiagnosticSnapshot.Mutation?.Corrections);

        source.Settle();
        coordinator.OnRenderedContentChanged(source);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        var mutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);
        Assert.Equal(TranscriptViewportMutationStatus.Completed, mutation.Status);
        Assert.Equal(1, mutation.Corrections);
        Assert.Equal(3, mutation.TerminalEpochs);
        Assert.InRange(Math.Abs(Top(protectedRow, scrollViewer) - protectedTop), 0, 1);
        Assert.Equal(0, realizations);
        Assert.Equal(0, host.AnchoringSuspensionDepth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RapidDetachedClicks_NewestExactLeasePreservesAnchorAndParity()
    {
        var watchdog = new ControlledWatchdogScheduler();
        var source = new ControlledGeometrySource();
        var first = CreateRow("first", 300);
        var tool = CreateRow("tool", 220, source);
        var last = CreateRow("last", 400);
        var rows = new StackPanel { Children = { first, tool, last } };
        var host = CreateAnchorHost(rows, first, tool, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        var protectedTop = Top(tool, scrollViewer);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: false,
            waitForWatchdog: watchdog.WaitAsync);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);
        var writes = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
        var expanded = false;

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool,
            isExpanding: true);
        source.Request();
        expanded = true;
        first.Height = 420;
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Passes >= 1);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - protectedTop), 0, 1);

        for (var index = 1; index < 5; index++)
        {
            coordinator.BeginViewportMutation(
                TranscriptViewportMutationKind.ToolExpansion,
                tool.AnchorKey,
                tool,
                isExpanding: !expanded);
            Assert.Equal(0, host.AnchoringSuspensionDepth);
            Assert.True(host.HasExactAnchorLease);
            expanded = !expanded;
            first.Height = expanded ? 420 : 300;
            coordinator.OnViewportContentChanged();
        }

        first.Height += 40;
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Passes >= 1);
        first.Height += 40;
        await LayoutAsync(window);
        source.AdvanceGeometry();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Passes >= 2);
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        var mutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);

        Assert.True(expanded);
        Assert.Equal(5, mutation.Generation);
        Assert.Equal(TranscriptViewportMutationStatus.Completed, mutation.Status);
        Assert.Equal(0, mutation.Corrections);
        Assert.Equal(writes, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.InRange(mutation.Passes, 3, 24);
        Assert.Equal(3, mutation.TerminalEpochs);
        Assert.Equal(5, watchdog.RequestCount);
        Assert.InRange(Math.Abs(Top(tool, scrollViewer) - protectedTop), 0, 1);
        Assert.Equal(0, host.AnchoringSuspensionDepth);
        Assert.True(host.HasExactAnchorLease);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ManualScrollAfterFirstCorrection_CancelsLateGeometryAbsolutely()
    {
        var source = new ControlledGeometrySource();
        var first = CreateRow("first", 300);
        var protectedRow = CreateRow("protected", 220, source);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { first, protectedRow, last } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(scrollViewer, rows, anchorHost: null, isFollowingLatest: false);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            protectedRow.AnchorKey,
            protectedRow);
        source.Request();
        first.Height = 420;
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Corrections == 1);

        window.MouseMove(new Point(150, 120), RawInputModifiers.None);
        window.MouseWheel(new Point(150, 120), new Vector(0, 1), RawInputModifiers.None);
        var userOffset = scrollViewer.Offset.Y;
        var writesAfterCancellation = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
        Assert.Equal(
            TranscriptViewportMutationStatus.CanceledByAuthority,
            coordinator.DiagnosticSnapshot.Mutation?.Status);

        first.Height = 500;
        source.AdvanceGeometry();
        source.Settle();
        coordinator.OnRenderedContentChanged(source);
        await LayoutAsync(window);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(userOffset, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(writesAfterCancellation, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(
            TranscriptViewportMutationStatus.CanceledByAuthority,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SelectionAutoscrollAfterFirstCorrectionRevokesMutationAuthority()
    {
        var source = new ControlledGeometrySource();
        var selectable = new SelectableTextBlock { Text = new string('s', 500) };
        var first = CreateRow("first", 300);
        var protectedRow = CreateRow("protected", 220, source);
        var last = CreateRow("last", 500, selectable);
        var rows = new StackPanel { Children = { first, protectedRow, last } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(scrollViewer, rows, anchorHost: null, isFollowingLatest: false);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            protectedRow.AnchorKey,
            protectedRow);
        source.Request();
        first.Height = 420;
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () => coordinator.DiagnosticSnapshot.Mutation?.Corrections == 1);
        var authorityBeforeSelection = coordinator.DiagnosticSnapshot.AuthorityRevision;

        selectable.SelectAll();
        scrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y + 24);
        var selectionOffset = scrollViewer.Offset.Y;
        var writesAfterSelection = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;

        Assert.Equal(
            TranscriptViewportMutationStatus.CanceledByAuthority,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        Assert.True(coordinator.DiagnosticSnapshot.AuthorityRevision > authorityBeforeSelection);

        first.Height = 500;
        source.AdvanceGeometry();
        source.Settle();
        await LayoutAsync(window);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(selectionOffset, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(writesAfterSelection, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(
            TranscriptViewportMutationStatus.CanceledByAuthority,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AlternatingGeometry_TerminatesAsCycleWithoutLeakingAuthority()
    {
        var source = new ControlledGeometrySource();
        var first = CreateRow("first", 300);
        var protectedRow = CreateRow("protected", 220, source);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { first, protectedRow, last } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(scrollViewer, rows, anchorHost: null, isFollowingLatest: false);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            protectedRow.AnchorKey,
            protectedRow);
        source.Request();
        var heights = new[] { 420d, 460d, 420d, 460d };
        foreach (var height in heights)
        {
            var previousPass = coordinator.DiagnosticSnapshot.Mutation?.Passes ?? 0;
            first.Height = height;
            await LayoutAsync(window);
            await PumpUntilAsync(window, () =>
                coordinator.DiagnosticSnapshot.Mutation?.Status
                    == TranscriptViewportMutationStatus.CycleDetected
                || coordinator.DiagnosticSnapshot.Mutation?.Passes > previousPass);
        }

        var mutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);
        Assert.True(
            mutation.Status == TranscriptViewportMutationStatus.CycleDetected,
            $"Expected an A-B-A-B cycle, observed: {mutation.GeometryCycle}");
        Assert.InRange(mutation.Passes, 4, 24);
        Assert.Equal(0, coordinator.DiagnosticSnapshot.AnchoringSuspensionDepth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PendingGeometry_ResizeAcrossBreakpointAndScrollbarThresholdStaysNative()
    {
        var source = new ControlledGeometrySource();
        var content = CreateRow("content", 520, source);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { content, tail } };
        var host = CreateAnchorHost(rows, content, tail);
        var scrollViewer = new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = new Window { Width = 521, Height = 300, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, MaxOffset(scrollViewer));
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(scrollViewer, rows, host, isFollowingLatest: true);
        source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);
        var writes = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;

        coordinator.BeginViewportMutation(TranscriptViewportMutationKind.StructuralLayout);
        source.Request();
        window.Width = 519;
        window.Height = 260;
        coordinator.OnViewportContentChanged();
        await LayoutAsync(window);
        window.Height = 240;
        await LayoutAsync(window);
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.True(MaxOffset(scrollViewer) > 0);
        Assert.Equal(writes, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);

        coordinator.BeginViewportMutation(TranscriptViewportMutationKind.StructuralLayout);
        source.Request();
        content.Height = 120;
        window.Width = 521;
        window.Height = 500;
        coordinator.OnViewportContentChanged();
        await LayoutAsync(window);
        source.Settle();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await LayoutAsync(window);

        Assert.Equal(0, MaxOffset(scrollViewer), precision: 3);
        Assert.Equal(writes, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(TranscriptViewportMutationStatus.Completed, coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BlockedOlderPage_ExpansionSupersedesItsPlacementAuthority()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateRow("first", 300);
        var protectedRow = CreateRow("protected", 220);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { first, protectedRow, last } };
        var host = CreateAnchorHost(rows, first, protectedRow, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
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
            anchorHost: host);

        Assert.True(coordinator.QueueLoadOlderRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(host.IsAnchoringSuspended);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            protectedRow.AnchorKey,
            protectedRow);
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () =>
            coordinator.DiagnosticSnapshot.Mutation?.Status == TranscriptViewportMutationStatus.Completed);
        Assert.False(host.IsAnchoringSuspended);

        first.Height += 120;
        await LayoutAsync(window);
        var offsetAfterPageGeometry = scrollViewer.Offset.Y;
        var writesBeforePageCompletion = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(offsetAfterPageGeometry, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(writesBeforePageCompletion, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(0, host.AnchoringSuspensionDepth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BlockedNewerPage_ResizeSupersedesItsPlacementAuthority()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateRow("first", 300);
        var protectedRow = CreateRow("protected", 220);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { first, protectedRow, last } };
        var host = CreateAnchorHost(rows, first, protectedRow, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => false,
            loadOlderRowsAsync: (_, _) => Task.FromResult(false),
            canLoadNewerRows: () => true,
            loadNewerRowsAsync: async (_, cancellationToken) =>
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return true;
            },
            hasNewerRows: () => false,
            isFollowingLatest: () => false,
            anchorHost: host);

        Assert.True(coordinator.QueueLoadNewerRows());
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(host.IsAnchoringSuspended);

        coordinator.BeginViewportMutation(TranscriptViewportMutationKind.StructuralLayout);
        coordinator.OnViewportContentChanged();
        await PumpUntilAsync(window, () =>
            coordinator.DiagnosticSnapshot.Mutation?.Status == TranscriptViewportMutationStatus.Completed);
        Assert.False(host.IsAnchoringSuspended);

        first.Height += 120;
        await LayoutAsync(window);
        var offsetAfterPageGeometry = scrollViewer.Offset.Y;
        var writesBeforePageCompletion = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
        releaseLoad.TrySetResult();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(offsetAfterPageGeometry, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(writesBeforePageCompletion, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
        Assert.Equal(0, host.AnchoringSuspensionDepth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeferredMarkdownAboveDetachedRow_PreservesViewportThroughLateHeight()
    {
        var parseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var deferredSource = "## Deferred expansion\n\n"
                             + string.Join(
                                 "\n\n",
                                 Enumerable.Repeat("A wrapping paragraph that expands the fallback.", 60));
        var builder = new ObservableStringBuilder("Seed content.");
        var presenter = new StreamingMarkdownPresenter(() => StableMarkdownRenderer.Create(source =>
        {
            if (source.Contains("Deferred expansion", StringComparison.Ordinal))
            {
                parseStarted.TrySetResult();
                releaseParse.Task.GetAwaiter().GetResult();
            }

            return Markdown.Parse(source, pipeline);
        }))
        {
            MarkdownBuilder = builder,
        };
        var markdownRow = CreateAutoRow("markdown", presenter);
        var protectedRow = CreateRow("protected", 220);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { markdownRow, protectedRow, last } };
        var host = CreateAnchorHost(rows, markdownRow, protectedRow, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        window.Show();
        try
        {
            await PumpMarkdownUntilAsync(
                window,
                () => !presenter.IsGeometryPending
                      && presenter.GetVisualDescendants()
                          .OfType<StableMarkdownRenderer>()
                          .Any(renderer => renderer.RenderedSource == "Seed content."));
            var seedHeight = presenter.Bounds.Height;
            scrollViewer.Offset = new Vector(0, Math.Max(0, Top(protectedRow, scrollViewer) - 40));
            await LayoutAsync(window);
            var protectedTop = Top(protectedRow, scrollViewer);
            using var coordinator = CreateCoordinator(scrollViewer, rows, host, isFollowingLatest: false);
            presenter.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(presenter);

            coordinator.BeginViewportMutation(
                TranscriptViewportMutationKind.LiveTranscript,
                protectedRow.AnchorKey);
            builder.Clear();
            builder.Append(deferredSource);
            coordinator.OnViewportContentChanged();
            await parseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await PumpMarkdownUntilAsync(
                window,
                () => coordinator.DiagnosticSnapshot.Mutation?.Passes >= 1);

            Assert.True(presenter.IsGeometryPending);
            Assert.True(host.IsAnchoringSuspended);
            Assert.Equal(0, coordinator.DiagnosticSnapshot.Mutation?.Corrections);

            releaseParse.TrySetResult();
            await PumpMarkdownUntilAsync(
                window,
                () => !presenter.IsGeometryPending
                      && coordinator.DiagnosticSnapshot.Mutation?.Corrections >= 1
                      && presenter.GetVisualDescendants()
                          .OfType<StableMarkdownRenderer>()
                          .Any(renderer => renderer.RenderedSource.Contains(
                              "Deferred expansion",
                              StringComparison.Ordinal)));
            var renderer = Assert.Single(presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
            var naturalMutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
                coordinator.DiagnosticSnapshot.Mutation);
            Assert.True(presenter.Bounds.Height > seedHeight);
            Assert.True(naturalMutation.Corrections >= 1);
            Assert.InRange(Math.Abs(Top(protectedRow, scrollViewer) - protectedTop), 0, 1);
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
            await presenter.PendingRenderOperations.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                TranscriptViewportMutationStatus.Completed,
                coordinator.DiagnosticSnapshot.Mutation?.Status);

            var geometryRevisionBeforeLateHeight = presenter.GeometryRevision;
            coordinator.BeginViewportMutation(
                TranscriptViewportMutationKind.LiveTranscript,
                protectedRow.AnchorKey);
            renderer.Height = renderer.Bounds.Height + 70;
            await PumpMarkdownUntilAsync(
                window,
                () => coordinator.DiagnosticSnapshot.Mutation?.Status
                      == TranscriptViewportMutationStatus.Completed);
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

            var lateMutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
                coordinator.DiagnosticSnapshot.Mutation);
            Assert.Equal(naturalMutation.Generation + 1, lateMutation.Generation);
            Assert.Equal(TranscriptViewportMutationStatus.Completed, lateMutation.Status);
            Assert.True(lateMutation.Corrections >= 1);
            Assert.True(presenter.GeometryRevision > geometryRevisionBeforeLateHeight);
            Assert.InRange(Math.Abs(Top(protectedRow, scrollViewer) - protectedTop), 0, 1);
            Assert.Equal(0, host.AnchoringSuspensionDepth);
        }
        finally
        {
            releaseParse.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task InitialAnchorPlacement_WaitsForDeferredMarkdownGeometryAndWritesOnce()
    {
        var parseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var source = "## Deferred history\n\n"
                     + string.Join(
                         "\n\n",
                         Enumerable.Repeat(
                             "A wrapping paragraph that establishes final history geometry.",
                             70));
        var builder = new ObservableStringBuilder("Seed content.");
        var presenter = new StreamingMarkdownPresenter(() => StableMarkdownRenderer.Create(markdown =>
        {
            if (markdown.Contains("Deferred history", StringComparison.Ordinal))
            {
                parseStarted.TrySetResult();
                releaseParse.Task.GetAwaiter().GetResult();
            }
            return Markdown.Parse(markdown, pipeline);
        }))
        {
            MarkdownBuilder = builder,
        };
        var markdownRow = CreateAutoRow("markdown", presenter);
        var target = CreateRow("target", 120);
        var last = CreateRow("last", 500);
        var rows = new StackPanel { Children = { markdownRow, target, last } };
        var host = CreateAnchorHost(rows, markdownRow, target, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = scrollViewer };
        window.Show();
        try
        {
            using var coordinator = CreateCoordinator(
                scrollViewer,
                rows,
                host,
                isFollowingLatest: false);
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await PumpMarkdownUntilAsync(
                window,
                () => !presenter.IsGeometryPending
                      && presenter.GetVisualDescendants()
                          .OfType<StableMarkdownRenderer>()
                          .Any(renderer => renderer.RenderedSource == "Seed content."));
            builder.Clear();
            builder.Append(source);
            coordinator.QueuePlaceAnchorAfterLayout("target", completed.SetResult);
            await parseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await LayoutAsync(window);

            Assert.False(completed.Task.IsCompleted);
            Assert.True(presenter.IsGeometryPending);
            Assert.Equal(0, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);

            releaseParse.TrySetResult();
            await PumpMarkdownUntilAsync(
                window,
                () => completed.Task.IsCompleted && !presenter.IsGeometryPending);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
            Assert.InRange(Top(target, scrollViewer), 27, 29);
            var settledOffset = scrollViewer.Offset;
            var settledExtent = scrollViewer.Extent;
            for (var pass = 0; pass < 3; pass++)
            {
                await LayoutAsync(window);
            }
            Assert.Equal(1, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
            Assert.Equal(settledOffset, scrollViewer.Offset);
            Assert.Equal(settledExtent, scrollViewer.Extent);
        }
        finally
        {
            releaseParse.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulAnchoredPlacement_DoesNotReevaluatePagingBeforeReveal()
    {
        var row = CreateRow("anchor", 700);
        var rows = new StackPanel { Children = { row } };
        var scrollViewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        var olderLoadCount = 0;
        window.Show();
        await LayoutAsync(window);
        using var coordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            rows,
            canLoadOlderRows: () => true,
            loadOlderRowsAsync: (_, _) =>
            {
                olderLoadCount++;
                return Task.FromResult(false);
            },
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => false);

        coordinator.QueuePlaceAnchorAfterLayout(row.AnchorKey!);
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, olderLoadCount);
        await LayoutAsync(window);
        Assert.Equal(0, olderLoadCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LiveTranscriptMutation_JoinsActiveExplicitViewportAuthority()
    {
        var first = CreateRow("first", 300);
        var tool = CreateRow("tool", 220);
        var last = CreateRow("last", 400);
        var rows = new StackPanel { Children = { first, tool, last } };
        var host = CreateAnchorHost(rows, first, tool, last);
        var scrollViewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 300, Height = 240, Content = scrollViewer };
        window.Show();
        await LayoutAsync(window);
        scrollViewer.Offset = new Vector(0, 250);
        await LayoutAsync(window);
        using var coordinator = CreateCoordinator(
            scrollViewer,
            rows,
            host,
            isFollowingLatest: false);

        coordinator.BeginViewportMutation(
            TranscriptViewportMutationKind.ToolExpansion,
            tool.AnchorKey,
            tool);
        var explicitMutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);
        coordinator.BeginTranscriptMutation();
        var joinedMutation = Assert.IsType<TranscriptViewportMutationDiagnostic>(
            coordinator.DiagnosticSnapshot.Mutation);

        Assert.Equal(explicitMutation.Generation, joinedMutation.Generation);
        Assert.Equal(explicitMutation.AuthorityRevision, joinedMutation.AuthorityRevision);
        Assert.Equal(TranscriptViewportMutationKind.ToolExpansion, joinedMutation.Kind);
        Assert.Equal(tool.AnchorKey, joinedMutation.ProtectedAnchorKey);
        Assert.Equal(TranscriptViewportMutationMode.ToolExpansionNativeAnchor, joinedMutation.Mode);
        Assert.Equal(0, coordinator.DiagnosticSnapshot.AnchoringSuspensionDepth);
        Assert.True(coordinator.DiagnosticSnapshot.ExactAnchorLeaseActive);

        first.Height = 360;
        coordinator.OnViewportContentChanged();
        coordinator.OnTranscriptChanged();
        await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            TranscriptViewportMutationStatus.Completed,
            coordinator.DiagnosticSnapshot.Mutation?.Status);
        window.Close();
    }

    private static TranscriptScrollCoordinator CreateCoordinator(
        ScrollViewer scrollViewer,
        Control itemsControl,
        TranscriptScrollAnchorHost? anchorHost,
        bool isFollowingLatest,
        Func<object, Control?>? realizeAnchor = null,
        Func<Control?>? realizeTail = null,
        Func<CancellationToken, Task>? waitForWatchdog = null,
        TimeProvider? viewportMutationTimeProvider = null,
        Func<bool>? canLoadOlder = null,
        Func<object?, CancellationToken, Task<bool>>? loadOlder = null,
        Func<bool>? onDetached = null,
        Action<bool>? setJumpVisible = null)
        => new(
            scrollViewer,
            itemsControl,
            canLoadOlderRows: canLoadOlder ?? (() => false),
            loadOlderRowsAsync: loadOlder ?? ((_, _) => Task.FromResult(false)),
            canLoadNewerRows: () => false,
            loadNewerRowsAsync: (_, _) => Task.FromResult(false),
            hasNewerRows: () => false,
            isFollowingLatest: () => isFollowingLatest,
            setJumpToLatestVisible: setJumpVisible,
            onDetachedFromLatest: onDetached,
            realizeAnchorVisual: realizeAnchor,
            anchorHost: anchorHost,
            realizeTailVisual: realizeTail,
            waitForViewportMutationWatchdog: waitForWatchdog,
            viewportMutationTimeProvider: viewportMutationTimeProvider);

    private static TranscriptScrollAnchorHost CreateAnchorHost(
        Control content,
        params TranscriptRowPresenter[] candidates)
    {
        var host = new TranscriptScrollAnchorHost { Children = { content } };
        var provider = (IScrollAnchorProvider)host;
        foreach (var candidate in candidates)
        {
            provider.RegisterAnchorCandidate(candidate);
        }
        return host;
    }

    private static TranscriptRowPresenter CreateRow(
        string key,
        double height,
        Control? content = null,
        TranscriptAnchorItemRole role = TranscriptAnchorItemRole.Transient)
        => new()
        {
            AnchorKey = key,
            AnchorRole = role,
            Height = height,
            IsRepeaterHosted = true,
            Content = content ?? new Border(),
        };

    private static TranscriptRowPresenter CreateAutoRow(string key, Control content)
        => new()
        {
            AnchorKey = key,
            AnchorRole = TranscriptAnchorItemRole.Transient,
            IsRepeaterHosted = true,
            Content = content,
        };

    private static async Task LayoutAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task PumpUntilAsync(
        Window window,
        Func<bool> condition,
        Func<string>? diagnostic = null)
    {
        for (var pass = 0; pass < 160; pass++)
        {
            await LayoutAsync(window);
            await Task.Yield();
            if (condition())
            {
                return;
            }
        }

        Assert.Fail(diagnostic?.Invoke() ?? "The viewport mutation did not reach the deterministic gate.");
    }

    private static async Task PumpMarkdownUntilAsync(
        Window window,
        Func<bool> condition,
        Func<string>? diagnostic = null)
    {
        for (var pass = 0; pass < 200; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail(diagnostic?.Invoke() ?? "The Markdown geometry stage did not settle.");
    }

    private static double Top(Control control, Visual relativeTo)
        => Assert.IsType<Point>(control.TranslatePoint(default, relativeTo)).Y;

    private static double MaxOffset(ScrollViewer scrollViewer)
        => Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

    private static void AssertTailAnchored(ScrollViewer scrollViewer, Control tail)
    {
        Assert.Same(tail, scrollViewer.CurrentAnchor);
        Assert.InRange(Math.Abs(MaxOffset(scrollViewer) - scrollViewer.Offset.Y), 0, 1);
    }

    private sealed class ControlledGeometrySource : Border, ITranscriptGeometrySource
    {
        public long RequestedRevision { get; private set; }

        public long SettledRevision { get; private set; }

        public long GeometryRevision { get; private set; }

        public bool IsGeometryPending => SettledRevision < RequestedRevision;

        public event EventHandler? GeometryChanged;

        public void Request()
        {
            RequestedRevision++;
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AdvanceGeometry()
        {
            GeometryRevision++;
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Settle()
        {
            SettledRevision = RequestedRevision;
            GeometryRevision++;
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ControlledWatchdogScheduler
    {
        private readonly List<TaskCompletionSource> _requests = [];

        public int RequestCount => _requests.Count;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(request);
            await request.Task.WaitAsync(cancellationToken);
        }

        public void ExpireLatest()
        {
            var request = _requests.LastOrDefault(candidate => !candidate.Task.IsCompleted);
            Assert.NotNull(request);
            request.TrySetResult();
        }
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private readonly object _syncRoot = new();
        private readonly HashSet<ManualTimer> _timers = [];

        public int TimerCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _timers.Count;
                }
            }
        }

        public TimeSpan LastDueTime { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_syncRoot)
            {
                LastDueTime = dueTime;
                _timers.Add(timer);
            }
            return timer;
        }

        public void FireAll()
        {
            ManualTimer[] timers;
            lock (_syncRoot)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_syncRoot)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimerTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private readonly object _syncRoot = new();
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_syncRoot)
                {
                    return !_disposed;
                }
            }

            public void Dispose()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }
                owner.Remove(this);
                callback(state);
            }
        }
    }
}

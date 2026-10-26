extern alias AgentApp;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class TranscriptPagingProgressTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TailPagingOwnsRealizedPlacementUntilMarkdownSettlesOrUserTakesOver(bool cancelWithInput)
    {
        using var releaseParser = new ManualResetEventSlim();
        var parserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StreamingMarkdownPresenter? markdown = null;
        var tail = new TranscriptRowPresenter
        {
            AnchorKey = "tail", AnchorRole = TranscriptAnchorItemRole.TailSentinel,
            IsRepeaterHosted = true, Content = new Border { Height = 1 },
        };
        var items = new System.Collections.ObjectModel.ObservableCollection<TranscriptRowPresenter>(
            Enumerable.Range(0, 100).Select(index => new TranscriptRowPresenter
            {
                AnchorKey = $"row:{index}", IsRepeaterHosted = true, Content = new Border { Height = 80 },
            }).Append(tail));
        var repeater = new ItemsRepeater
        {
            ItemsSource = items,
            Layout = new Avalonia.Layout.StackLayout(),
        };
        var host = new TranscriptScrollAnchorHost { Children = { repeater } };
        var viewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var hasNewerRows = true;
            var following = false;
            var tailRealizations = 0;
            using var coordinator = new TranscriptScrollCoordinator(viewer, repeater,
                canLoadOlderRows: () => false, loadOlderRowsAsync: (_, _) => Task.FromResult(false),
                canLoadNewerRows: () => hasNewerRows,
                loadNewerRowsAsync: (_, _) =>
                {
                    markdown = new StreamingMarkdownPresenter(() => StableMarkdownRenderer.Create(source =>
                    {
                        parserStarted.TrySetResult();
                        releaseParser.Wait(TimeSpan.FromSeconds(10));
                        return Markdig.Markdown.Parse(source);
                    })) { MarkdownBuilder = new ObservableStringBuilder("### Newest response\n\n" + new string('x', 2000)) };
                    items.Insert(items.Count - 1, new TranscriptRowPresenter
                    {
                        AnchorKey = "pending", IsRepeaterHosted = true, Content = markdown,
                    });
                    hasNewerRows = false;
                    return Task.FromResult(true);
                },
                hasNewerRows: () => hasNewerRows, isFollowingLatest: () => following,
                onDetachedFromLatest: () => { following = false; return true; },
                onReachedLatest: () => { following = true; return true; }, anchorHost: host,
                realizeTailVisual: () =>
                {
                    tailRealizations++;
                    var element = repeater.GetOrCreateElement(items.Count - 1);
                    repeater.UpdateLayout();
                    element.BringIntoView();
                    repeater.UpdateLayout();
                    return element;
                });
            Assert.True(coordinator.QueueLoadNewerRows(resumeFollowingWhenCaughtUp: true));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(tailRealizations > 0); // An estimated extent is not a realized tail.
            try { await parserStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Markdown={markdown?.IsRenderPending},attached={markdown?.IsAttachedToVisualTree()},tail={repeater.TryGetElement(items.Count - 1)?.Bounds}; {coordinator.DiagnosticSnapshot}", exception);
            }
            Assert.True(coordinator.DiagnosticSnapshot.BottomPlacementLockActive);
            Assert.False(following);
            long writesAfterInput = 0;
            if (cancelWithInput)
            {
                window.MouseWheel(new Point(120, 120), new Vector(0, 1), RawInputModifiers.None);
                writesAfterInput = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;
                await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.False(coordinator.DiagnosticSnapshot.BottomPlacementLockActive);
                Assert.False(host.IsAnchoringSuspended);
                Assert.False(releaseParser.IsSet);
            }
            releaseParser.Set();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.IsType<StreamingMarkdownPresenter>(markdown).PendingRenderOperations.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(!cancelWithInput, following);
            Assert.False(coordinator.DiagnosticSnapshot.BottomPlacementLockActive);
            Assert.False(host.IsAnchoringSuspended);
            if (cancelWithInput)
                Assert.Equal(writesAfterInput, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
            else
            {
                Assert.True(host.IsTailVisible(viewer));
                Assert.InRange(Math.Abs(viewer.Offset.Y - viewer.Extent.Height + viewer.Viewport.Height), 0, 1);
            }
        }
        finally { releaseParser.Set(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReflowCannotAutomaticallyPageAgainstTheReadersScrollDirection(bool towardHistory)
    {
        var content = new Border { Height = 600 };
        var viewer = new ScrollViewer { Content = content };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            viewer.Offset = new Vector(0, 180);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var oppositePages = 0;
            Task<bool> LoadOpposite(object? _, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                oppositePages++;
                return Task.FromResult(false);
            }
            using var coordinator = new TranscriptScrollCoordinator(viewer,
                canLoadOlderRows: () => !towardHistory, loadOlderRowsAsync: LoadOpposite,
                canLoadNewerRows: () => towardHistory, loadNewerRowsAsync: LoadOpposite,
                hasNewerRows: () => towardHistory, isFollowingLatest: () => false);
            window.MouseWheel(new Point(120, 120), new Vector(0, towardHistory ? 1 : -1), RawInputModifiers.None);
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, oppositePages);
            content.Height = 100;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, oppositePages);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FocusPinnedRecycledAnchorMustBeLaidOutBeforeItsPositionIsRestored()
    {
        var items = Enumerable.Range(0, 100).Select(index => $"row:{index}").ToArray();
        var repeater = new ItemsRepeater
        {
            ItemsSource = items,
            Layout = new Avalonia.Layout.StackLayout(),
            ItemTemplate = new FuncDataTemplate<string>((_, _) =>
            {
                var row = new TranscriptRowPresenter
                {
                    Height = 80, Content = new Button { Content = "Focus" }, IsRepeaterHosted = true,
                };
                row.Bind(TranscriptRowPresenter.AnchorKeyProperty, new Binding());
                return row;
            }),
        };
        var host = new TranscriptScrollAnchorHost { Children = { repeater } };
        var viewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            viewer.Offset = new Vector(0, 2400);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var target = Assert.IsType<TranscriptRowPresenter>(repeater.TryGetElement(30));
            Assert.True(Assert.IsType<Button>(target.Content).Focus());
            var originalTop = target.TranslatePoint(default, viewer)!.Value.Y;
            viewer.Offset = default;
            for (var pass = 0; pass < 3; pass++)
            {
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            Assert.Same(target, repeater.TryGetElement(30));
            Assert.True(target.Bounds.Y < -10000, $"Pinned bounds={target.Bounds}; offset={viewer.Offset.Y}");
            using var coordinator = new TranscriptScrollCoordinator(viewer, repeater,
                canLoadOlderRows: () => false, loadOlderRowsAsync: (_, _) => Task.FromResult(false),
                canLoadNewerRows: () => false, loadNewerRowsAsync: (_, _) => Task.FromResult(false),
                hasNewerRows: () => false, isFollowingLatest: () => false, anchorHost: host,
                realizePreservedAnchorVisual: key =>
                {
                    var element = repeater.GetOrCreateElement(Array.IndexOf(items, key));
                    repeater.UpdateLayout();
                    using (host.SuppressPreservedRealizationNavigation()) element.BringIntoView();
                    repeater.UpdateLayout();
                    return element;
                });
            coordinator.RestoreViewportAnchor(new TranscriptViewportAnchorData(items[30], 2400, 0, originalTop));
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(target.Bounds.Height > 0);
            Assert.InRange(Math.Abs(target.TranslatePoint(default, viewer)!.Value.Y - originalTop), 0, 1);
            Assert.Equal(0, coordinator.DiagnosticSnapshot.AnchoringSuspensionDepth);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PreservedRealizationRetiresTheRepeaterAnchorWithoutNavigatingTheScroller()
    {
        var repeater = new ItemsRepeater
        {
            ItemsSource = Enumerable.Range(0, 100).Select(_ => new object()).ToArray(),
            Layout = new Avalonia.Layout.StackLayout(),
            ItemTemplate = new FuncDataTemplate<object>((row, _) => new TranscriptRowPresenter
            {
                AnchorKey = row, Height = 80, Content = new Border(), IsRepeaterHosted = true,
            }),
        };
        var host = new TranscriptScrollAnchorHost { Children = { repeater } };
        var viewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            viewer.Offset = new Vector(0, 2000);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var offset = viewer.Offset.Y;
            using var suspended = host.SuspendAnchoring();
            var anchor = repeater.GetOrCreateElement(0);
            repeater.UpdateLayout();
            using (host.SuppressPreservedRealizationNavigation()) anchor.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Null(typeof(ItemsRepeater).GetProperty("MadeAnchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(repeater));
            Assert.Equal(offset, viewer.Offset.Y);

            // The suppression is local to preservation; explicit navigation works.
            var destination = repeater.GetOrCreateElement(90);
            repeater.UpdateLayout();
            destination.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(viewer.Offset.Y > offset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PresentationReorderingKeepsRepeaterIndicesConsistent()
    {
        var first = new object();
        var second = new object();
        var third = new object();
        var source = new TranscriptObservableCollection<object>([first, second, third]);
        var repeater = new ItemsRepeater
        {
            ItemsSource = source,
            Layout = new Avalonia.Layout.StackLayout(),
            ItemTemplate = new FuncDataTemplate<object>((row, _) => new TranscriptRowPresenter
            {
                AnchorKey = row, Height = 70, Content = new Border(), IsRepeaterHosted = true,
            }),
        };
        var window = new Window { Width = 320, Height = 300, Content = new ScrollViewer { Content = repeater } };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            source.ReconcilePresentation([third, first, second]);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            for (var index = 0; index < source.Count; index++)
                Assert.Same(source[index], Assert.IsType<TranscriptRowPresenter>(repeater.TryGetElement(index)).DataContext);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SurvivingPrimaryAnchorDoesNotRealizeTrimmedFallbacks()
    {
        var primary = new TranscriptRowPresenter { AnchorKey = "primary", Height = 80, Content = new Border() };
        var fallback = new TranscriptRowPresenter { AnchorKey = "fallback", Height = 80, Content = new Border() };
        var rows = new StackPanel { Children = { primary, fallback, new Border { Height = 600 } } };
        var viewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var realizations = new List<object>();
            var pages = 0;
            using var coordinator = new TranscriptScrollCoordinator(viewer, rows,
                canLoadOlderRows: () => pages == 0,
                loadOlderRowsAsync: async (_, _) =>
                {
                    pages++;
                    rows.Children.Remove(fallback);
                    rows.Children.Insert(0, new Border { Height = 100 });
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    return true;
                },
                canLoadNewerRows: () => false, loadNewerRowsAsync: (_, _) => Task.FromResult(false),
                hasNewerRows: () => false, isFollowingLatest: () => false,
                realizeAnchorVisual: key => { realizations.Add(key); return null; });
            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, pages);
            Assert.Empty(realizations);
            Assert.InRange(Math.Abs(primary.TranslatePoint(default, viewer)!.Value.Y), 0, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PagesContinueAtTheEdgeUntilThereIsContentWithoutAnotherScroll(bool older)
    {
        var rows = new StackPanel { Children = { new Border { Height = 20 } } };
        var viewer = new ScrollViewer { Content = rows };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            var pages = 0;
            using var coordinator = new TranscriptScrollCoordinator(viewer, rows,
                canLoadOlderRows: () => older && pages < 3,
                loadOlderRowsAsync: LoadPage,
                canLoadNewerRows: () => !older && pages < 3,
                loadNewerRowsAsync: LoadPage,
                hasNewerRows: () => !older && pages < 3,
                isFollowingLatest: () => false);

            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(3, pages);
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height);

            async Task<bool> LoadPage(object? _, CancellationToken cancellationToken)
            {
                pages++;
                // The first two server pages advance a cursor but project no rows.
                if (pages == 3) rows.Children.Insert(0, new Border { Height = 600 });
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render, cancellationToken);
                return true;
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CursorOnlyPagesPauseAtTheBudgetAndResumeOnlyAfterInteraction(bool older)
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 20 } };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            var pages = 0;
            var failures = 0;
            Task<bool> LoadPage(object? _, CancellationToken token) { token.ThrowIfCancellationRequested(); pages++; return Task.FromResult(true); }
            using var coordinator = new TranscriptScrollCoordinator(viewer,
                canLoadOlderRows: () => older, loadOlderRowsAsync: LoadPage,
                canLoadNewerRows: () => !older, loadNewerRowsAsync: LoadPage,
                hasNewerRows: () => !older, isFollowingLatest: () => false,
                pagingFailed: error => { Assert.IsType<InvalidDataException>(error); failures++; });
            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(8, pages);
            Assert.Equal(1, failures);
            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(8, pages);

            window.MouseWheel(new Point(100, 100), new Vector(0, older ? 1 : -1), RawInputModifiers.None);
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(16, pages);
            Assert.Equal(2, failures);
            coordinator.SetPresentationActive(false);
            coordinator.SetPresentationActive(true);
            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(24, pages);
            Assert.Equal(3, failures);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeactivatedPageCannotContinueItsOldEdge()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewer = new ScrollViewer { Content = new Border { Height = 20 } };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            var pages = 0;
            using var coordinator = new TranscriptScrollCoordinator(viewer,
                canLoadOlderRows: () => true,
                loadOlderRowsAsync: async (_, _) => { pages++; started.TrySetResult(); await release.Task; return true; },
                canLoadNewerRows: () => false, loadNewerRowsAsync: (_, _) => Task.FromResult(false),
                hasNewerRows: () => false, isFollowingLatest: () => false);
            coordinator.ReevaluatePagingEdges();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            coordinator.SetPresentationActive(false);
            release.TrySetResult();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, pages);
        }
        finally { release.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedPageDoesNotStartAnAutomaticRetryLoop()
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 20 } };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            var pages = 0;
            Exception? reported = null;
            using var coordinator = new TranscriptScrollCoordinator(viewer,
                canLoadOlderRows: () => true,
                loadOlderRowsAsync: (_, _) =>
                {
                    pages++;
                    throw new IOException("Synthetic paging failure");
                },
                canLoadNewerRows: () => false,
                loadNewerRowsAsync: (_, _) => Task.FromResult(false),
                hasNewerRows: () => false,
                isFollowingLatest: () => false,
                pagingFailed: error => reported = error);
            coordinator.ReevaluatePagingEdges();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, pages);
            Assert.IsType<IOException>(reported);
        }
        finally { window.Close(); }
    }
}

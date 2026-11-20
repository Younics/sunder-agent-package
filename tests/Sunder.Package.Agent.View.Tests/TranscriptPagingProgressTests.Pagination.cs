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
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class TranscriptPagingProgressTests
{
    [AvaloniaFact]
    public async Task PagePublicationKeepsAvailableContentAnchoredWhileNewMarkdownParsingIsBlocked()
    {
        using var releaseParser = new ManualResetEventSlim();
        var parserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TranscriptRowPresenter { AnchorKey = "first", Height = 200, Content = new Border() };
        var reading = new TranscriptRowPresenter { AnchorKey = "reading", Height = 100, Content = new TextBlock { Text = "Previously visible message" } };
        var rows = new StackPanel { Children = { first, reading, new Border { Height = 600 } } };
        var host = new TranscriptScrollAnchorHost { Children = { rows } };
        var viewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 360, Height = 300, Content = viewer };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            viewer.Offset = new Vector(0, 180);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var top = reading.TranslatePoint(default, viewer)!.Value.Y;
            var pages = 0;
            using var coordinator = new TranscriptScrollCoordinator(viewer, rows,
                canLoadOlderRows: () => pages == 0,
                loadOlderRowsAsync: (authority, _) =>
                {
                    var publication = Assert.IsType<TranscriptPageAnchorAuthority>(authority);
                    publication.PreparePublication();
                    var markdown = new StreamingMarkdownPresenter(() => StableMarkdownRenderer.Create(source =>
                    {
                        parserStarted.TrySetResult();
                        releaseParser.Wait(TimeSpan.FromSeconds(10));
                        return Markdig.Markdown.Parse(source);
                    })) { MarkdownBuilder = new ObservableStringBuilder("### Older message\n\n" + string.Join("\n\n", Enumerable.Repeat("A wrapping historical paragraph.", 12))) };
                    rows.Children.Insert(0, new TranscriptRowPresenter { AnchorKey = "older", Content = markdown });
                    publication.CompletePublication();
                    pages++;
                    return Task.FromResult(true);
                },
                canLoadNewerRows: () => false, loadNewerRowsAsync: (_, _) => Task.FromResult(false),
                hasNewerRows: () => false, isFollowingLatest: () => false, anchorHost: host);
            Assert.True(coordinator.QueueLoadOlderRows());
            await parserStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.False(releaseParser.IsSet);
            Assert.InRange(Math.Abs(reading.TranslatePoint(default, viewer)!.Value.Y - top), 0, 1);
            Assert.Equal(1, viewer.Opacity);
            releaseParser.Set();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.InRange(Math.Abs(reading.TranslatePoint(default, viewer)!.Value.Y - top), 0, 1);
            Assert.False(host.IsAnchoringSuspended);
        }
        finally { releaseParser.Set(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedPagePublicationPreservesTheLatestVisibleRowThroughTrimAtEveryRender(bool older)
    {
        var all = Enumerable.Range(0, 90).Select(index => new PageRow($"row:{index}", 60 + index % 5 * 12)).ToArray();
        var source = new TranscriptObservableCollection<PageRow>(older ? all[30..] : all[..60]);
        var activity = new PageRow("activity", 1, TranscriptAnchorItemRole.Persistent);
        var tail = new PageRow("tail", 1, TranscriptAnchorItemRole.TailSentinel);
        using var projection = new TranscriptActivityProjection<PageRow>(source, _ => null, (row, _) => row, activity, tail);
        var repeater = new ItemsRepeater
        {
            ItemsSource = projection.Items, Layout = new Avalonia.Layout.StackLayout(),
            ItemTemplate = new FuncDataTemplate<PageRow>((_, _) =>
            {
                var row = new TranscriptRowPresenter { IsRepeaterHosted = true };
                row.Bind(TranscriptRowPresenter.AnchorKeyProperty, new Binding(nameof(PageRow.Key)));
                row.Bind(TranscriptRowPresenter.AnchorRoleProperty, new Binding(nameof(PageRow.Role)));
                row.Bind(TranscriptRowPresenter.HeightProperty, new Binding(nameof(PageRow.Height)));
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(PageRow.Key)));
                row.Content = text;
                return row;
            }, supportsRecycling: true),
        };
        var host = new TranscriptScrollAnchorHost { Children = { repeater } };
        var viewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 360, Height = 300, Content = viewer };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Show();
        try
        {
            await Layout();
            viewer.Offset = new Vector(0, 1000);
            await Layout();
            var pages = 0;
            object? publicationKey = null;
            TranscriptPageAnchorAuthority? authority = null;
            using var coordinator = new TranscriptScrollCoordinator(viewer, repeater,
                canLoadOlderRows: () => older && pages == 0, loadOlderRowsAsync: Load,
                canLoadNewerRows: () => !older && pages == 0, loadNewerRowsAsync: Load,
                hasNewerRows: () => !older && pages == 0, isFollowingLatest: () => false, anchorHost: host);
            Assert.True(older ? coordinator.QueueLoadOlderRows() : coordinator.QueueLoadNewerRows());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.MouseWheel(new Point(150, 150), new Vector(0, older ? -3 : 3), RawInputModifiers.None);
            await Layout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);

            var candidates = repeater.GetVisualDescendants().OfType<TranscriptRowPresenter>()
                .Where(row => row.AnchorRole == TranscriptAnchorItemRole.Transient && TranscriptRowPresenter.HasLayoutGeometry(row))
                .Select(row => (Row: row, Top: row.TranslatePoint(default, viewer)!.Value.Y))
                .Where(row => row.Top >= 0 && row.Top < viewer.Viewport.Height).OrderBy(row => row.Top).ToArray();
            Assert.NotEmpty(candidates);
            var key = Assert.IsType<string>(candidates[0].Row.AnchorKey);
            var index = projection.Items.ToList().FindIndex(row => row.Key == key);
            var visual = Assert.IsType<TranscriptRowPresenter>(repeater.TryGetElement(index));
            var top = visual.TranslatePoint(default, viewer)!.Value.Y;
            Assert.InRange(top, 0, viewer.Viewport.Height);
            var badFrames = new List<string>();
            void ObserveFrame()
            {
                var retainedIndex = projection.Items.ToList().FindIndex(row => row.Key == key);
                var retained = retainedIndex < 0 ? null : repeater.TryGetElement(retainedIndex);
                var currentTop = retained?.TranslatePoint(default, viewer)?.Y;
                if (retained is null || viewer.Opacity != 1 || currentTop is null || Math.Abs(currentTop.Value - top) > 1)
                    badFrames.Add($"anchor={key},publication={publicationKey},top={currentTop},expected={top},offset={viewer.Offset.Y},extent={viewer.Extent.Height}; {coordinator.DiagnosticSnapshot}");
            }
            var observing = true;
            var frameQueued = false;
            void QueueFrame(object? _, EventArgs __)
            {
                if (frameQueued || !observing) return;
                frameQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    frameQueued = false;
                    if (observing) ObserveFrame();
                }, DispatcherPriority.Render);
            }
            viewer.LayoutUpdated += QueueFrame;
            try
            {
                release.TrySetResult();
                await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
                await Layout();
                var retainedIndex = projection.Items.ToList().FindIndex(row => row.Key == key);
                Assert.True(badFrames.Count == 0, string.Join('\n', badFrames.Take(12)));
                Assert.Same(visual, repeater.TryGetElement(retainedIndex));
                Assert.Equal(60, source.Count);
                Assert.False(coordinator.DiagnosticSnapshot.ExactAnchorLeaseActive);
                Assert.False(host.IsAnchoringSuspended);
            }
            finally { observing = false; viewer.LayoutUpdated -= QueueFrame; }

            async Task<bool> Load(object? protectedAnchor, CancellationToken token)
            {
                authority = Assert.IsType<TranscriptPageAnchorAuthority>(protectedAnchor);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                authority.PreparePublication();
                publicationKey = authority.ResolveCurrentAnchor().AnchorKey;
                var desired = TranscriptRowWindow.SelectRetainedRows(all, 60,
                    older ? AgentTranscriptTrimDirection.Newest : AgentTranscriptTrimDirection.Oldest,
                    authority.ResolveCurrentAnchor().AnchorKey, row => row.Key);
                using (source.DeferReconciliation()) source.ReplaceAll(desired);
                authority.CompletePublication();
                pages++;
                return true;
            }
            async Task Layout()
            {
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }
        finally { release.TrySetResult(); window.Close(); }
    }

    private sealed record PageRow(string Key, double Height, TranscriptAnchorItemRole Role = TranscriptAnchorItemRole.Transient);
}

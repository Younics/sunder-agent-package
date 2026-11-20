extern alias AgentApp;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using Markdig;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class TranscriptViewportMutationTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancelledInitialPlacement_IgnoresLateTranscriptEventsUntilExplicitRetry(bool stillLoading, bool hideAncestor)
    {
        var row = CreateRow("row", 1000);
        var tail = CreateRow("tail", 1, role: TranscriptAnchorItemRole.TailSentinel);
        var rows = new StackPanel { Children = { row, tail } };
        var host = CreateAnchorHost(rows, row, tail);
        var viewer = new ScrollViewer { Content = host };
        var owner = new Grid { Children = { viewer } };
        var loading = true;
        using var behavior = new TranscriptViewBehavior(owner, viewer, rows, new Button(),
            () => false, (_, _) => Task.FromResult(false),
            () => false, (_, _) => Task.FromResult(false), () => false, () => true,
            () => loading, () => true, () => true, () => true, () => true,
            anchorHost: host);
        var coordinator = Assert.IsType<TranscriptScrollCoordinator>(
            RenderedTranscriptGeometry.GetPrivateField(behavior, "_scrollCoordinator"));
        var ancestor = new Border { Child = owner };
        var window = new Window { Width = 320, Height = 240, Content = ancestor };
        window.Show();
        try
        {
            using var cancellation = new CancellationTokenSource();
            behavior.MarkInitialPlacementPending(cancellation.Token);
            var presentation = behavior.WaitForInitialPresentationAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => presentation);
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            var cancelled = behavior.DiagnosticSnapshot;
            var writes = RenderedTranscriptGeometry.GetCoordinatorProgrammaticOffsetWrites(coordinator);
            var authority = RenderedTranscriptGeometry.GetCoordinatorAuthorityRevision(coordinator);
            var presentationRevision = coordinator.DiagnosticSnapshot.PresentationRevision;

            loading = stillLoading;
            behavior.OnTranscriptSelectionChanging();
            behavior.OnTranscriptChanging(isPaging: false);
            behavior.OnTranscriptChanged();
            Assert.Equal(authority, RenderedTranscriptGeometry.GetCoordinatorAuthorityRevision(coordinator));
            var visibilityOwner = hideAncestor ? (Control)ancestor : owner;
            visibilityOwner.IsVisible = false;
            visibilityOwner.IsVisible = true;
            Assert.Equal(presentationRevision + 2, coordinator.DiagnosticSnapshot.PresentationRevision);
            Assert.Equal(authority + 2, coordinator.DiagnosticSnapshot.AuthorityRevision);
            coordinator.SetPresentationActive(true);
            Assert.Equal(presentationRevision + 2, coordinator.DiagnosticSnapshot.PresentationRevision);
            Assert.Equal(authority + 2, coordinator.DiagnosticSnapshot.AuthorityRevision);
            loading = false;
            behavior.OnTranscriptChanged();
            await LayoutAsync(window);
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(behavior.DiagnosticSnapshot.InitialPlacementPending);
            Assert.Equal(cancelled.InitialPlacementVersion, behavior.DiagnosticSnapshot.InitialPlacementVersion);
            Assert.Equal(writes, RenderedTranscriptGeometry.GetCoordinatorProgrammaticOffsetWrites(coordinator));
            // Visibility changes have their own authority; late transcript events must not restart placement.
            Assert.False(RenderedTranscriptGeometry.GetPrivateBoolean(coordinator, "_bottomPlacementLockActive"));
            Assert.Equal(1, viewer.Opacity);

            behavior.MarkInitialPlacementPending();
            behavior.OnTranscriptChanged();
            await behavior.WaitForInitialPresentationAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(behavior.DiagnosticSnapshot.InitialPlacementVersion > cancelled.InitialPlacementVersion);
            Assert.True(RenderedTranscriptGeometry.GetCoordinatorProgrammaticOffsetWrites(coordinator) > writes);
            Assert.Equal(1, viewer.Opacity);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StalledInitialGeometryRevealsFallbackAndCompletesPlacement()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var markdown = new StreamingMarkdownPresenter(() => StableMarkdownRenderer.Create(source =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return Markdown.Parse(source);
        })) { MarkdownBuilder = new ObservableStringBuilder("Visible fallback while Markdown is delayed.") };
        var first = CreateAutoRow("first", markdown);
        var target = CreateRow("target", 300);
        var rows = new StackPanel { Children = { first, target } };
        var host = CreateAnchorHost(rows, first, target);
        var viewer = new ScrollViewer { Content = host };
        var owner = new Grid { Children = { viewer } };
        using var behavior = new TranscriptViewBehavior(owner, viewer, rows, new Button(),
            () => false, (_, _) => Task.FromResult(false),
            () => false, (_, _) => Task.FromResult(false), () => false, () => false,
            () => false, () => true, () => true, () => true, () => true,
            pagingFailed: error => failure.TrySetResult(error), anchorHost: host);
        var window = new Window { Width = 320, Height = 240, Content = owner };
        window.Show();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var completions = behavior.DiagnosticSnapshot.CompletionCallbacks;
            behavior.SetInitialPlacementAnchor("target");
            await behavior.WaitForInitialPresentationAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(1, viewer.Opacity);
            Assert.False(behavior.DiagnosticSnapshot.InitialPlacementPending);
            Assert.Equal(completions + 1, behavior.DiagnosticSnapshot.CompletionCallbacks);
            Assert.IsType<TimeoutException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            release.TrySetResult();
            await Dispatcher.UIThread.InvokeAsync(window.Close, DispatcherPriority.Background);
        }
    }
}

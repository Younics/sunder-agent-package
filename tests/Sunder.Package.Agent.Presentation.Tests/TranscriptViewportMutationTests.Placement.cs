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

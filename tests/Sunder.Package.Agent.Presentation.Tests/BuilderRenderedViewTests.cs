extern alias BuilderApp;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;
using static Sunder.Package.Agent.Presentation.Tests.RenderedBuilderFixture;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class BuilderRenderedViewTests
{
    [AvaloniaFact]
    public async Task BuilderView_StretchesProjectRowsAndReflowsCompactActionsWithoutHorizontalOverflow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "builder-layout", new string('a', 100));
        var source = new BuilderProjectInspection { Kind = BuilderProjectKind.Node, ProjectFolder = folder,
            EntryPoint = Path.Combine(folder, "package.json"), PackageId = "local.layout", DisplayName = "A long project name for responsive layout",
            SourceRevision = "layout", CanEditDisplayName = true, CanEditPackageId = true,
            ToolchainAssessment = new(true, true, "Project pins require an explicit toolchain upgrade.") };
        await using var model = CreateColdBuilderViewModel(new BlockingBuilderProjectStore(), source);
        var row = new BuilderProjectViewModel(new("layout", source.DisplayName, source.PackageId, folder, source.EntryPoint,
            source.Kind, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        row.ApplyInspection(source);
        model.Projects.Add(row);
        model.ActivateProject(row);
        await model.RefreshSelectedProjectAsync();
        using var view = new BuilderView(model);
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        try
        {
            foreach (var width in new[] { 320d, 600d, 819d, 820d, 1200d })
            {
                window.Width = width;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var actions = Assert.Single(view.GetVisualDescendants().OfType<WrapPanel>(), panel => panel.Classes.Contains("header-actions"));
                Assert.Equal(width < 820 ? 2 : 0, Grid.GetRow(actions));
                Assert.Equal(width < 820 ? 0 : 1, Grid.GetColumn(actions));
                Assert.Equal(width < 820 ? 2 : 1, Grid.GetColumnSpan(actions));
                Assert.Equal(width < 820 ? HorizontalAlignment.Left : HorizontalAlignment.Right, actions.HorizontalAlignment);
                var details = Assert.Single(view.GetVisualDescendants().OfType<ScrollViewer>(), scroll => scroll.Name == "ProjectContentScroll");
                Assert.True(details.Extent.Width <= details.Viewport.Width + 0.5,
                    $"Builder details overflowed at {width}px: {details.Extent.Width} > {details.Viewport.Width}.");
                var settings = Assert.Single(view.GetVisualDescendants().OfType<Expander>(), expander => Equals(expander.Header, "Project settings"));
                Assert.False(settings.IsExpanded);
                var headerButtons = actions.GetVisualDescendants().OfType<Button>().ToArray();
                Assert.Contains(headerButtons, button => Equals(button.Content, "Load into Sunder"));
                Assert.DoesNotContain(headerButtons, button => Equals(button.Content, "Build") || Equals(button.Content, "Create Bundle"));
                var buildActions = view.GetVisualDescendants().OfType<WrapPanel>().Single(control => control.Name == "ProjectBuildActions");
                var notices = view.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "ProjectNotices");
                Assert.True(buildActions.TranslatePoint(default, view)!.Value.Y >= notices.TranslatePoint(default, view)!.Value.Y + notices.Bounds.Height);
                var editor = view.FindControl<Border>("BuilderEditorPane")!;
                Assert.InRange(Math.Abs(details.TranslatePoint(default, editor)!.Value.X + details.Bounds.Width - editor.Bounds.Width), 0, 0.5);
                var footer = view.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "ProjectEditorFooter");
                var back = footer.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Back"));
                var save = footer.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save"));
                Assert.InRange(Math.Abs(back.TranslatePoint(default, footer)!.Value.Y - save.TranslatePoint(default, footer)!.Value.Y), 0, 0.5);
                Assert.DoesNotContain(footer.GetVisualAncestors(), control => ReferenceEquals(control, details));
                Assert.Contains(footer.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Remove"));
                UiCapture.Save(window, $"maker-project-{width:0}");
                if (width >= 820)
                {
                    var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Classes.Contains("projects"));
                    var item = Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>());
                    Assert.Equal(HorizontalAlignment.Stretch, item.HorizontalContentAlignment);
                    Assert.True(Math.Abs(list.Bounds.Width - item.Bounds.Width) <= 1,
                        $"Project row does not fill its aligned list: {item.Bounds.Width} / {list.Bounds.Width}.");
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BuilderView_ConstructionIsColdAndWarmupNavigationShareOneLoad()
    {
        var store = new BlockingBuilderProjectStore();
        await using var viewModel = CreateColdBuilderViewModel(store);
        using var view = new BuilderView(viewModel);
        var context = new PackageViewNavigationContext("younics.sunder.package.maker", new Dictionary<string, string?>());

        Assert.Equal(0, store.LoadCount);
        var warmup = view.WarmupAsync().AsTask();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var navigation = view.OnNavigatedToAsync(context).AsTask();
        using var cancellation = new CancellationTokenSource();
        var canceledNavigation = view.OnNavigatedToAsync(context, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledNavigation);
        Assert.Equal(1, store.LoadCount);
        store.ReleaseLoad.TrySetResult();
        await Task.WhenAll(warmup, navigation);
        await view.OnNavigatedToAsync(context);

        Assert.Equal(1, store.LoadCount);
    }

    [AvaloniaFact]
    public async Task BuilderView_RetriesTransientWarmupFailure()
    {
        var store = new FailOnceBuilderProjectStore();
        await using var viewModel = CreateColdBuilderViewModel(store);
        using var view = new BuilderView(viewModel);

        await view.WarmupAsync();
        await view.WarmupAsync();

        Assert.Equal(2, store.LoadCount);
        Assert.DoesNotContain(
            "initialization failed",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task BuilderViewModel_DisposalDrainsBlockedInitializationBeforePersistenceDisposal()
    {
        var store = new BlockingBuilderProjectStore();
        var viewModel = CreateColdBuilderViewModel(store);
        var initialization = viewModel.InitializeAsync();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = viewModel.DisposeAsync().AsTask();
        await store.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposal.IsCompleted);

        store.ReleaseCancellationCleanup.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [AvaloniaFact]
    public async Task BuilderView_CloseDoesNotCancelActivationOwnedInitialization()
    {
        var store = new BlockingBuilderProjectStore();
        await using var model = CreateColdBuilderViewModel(store);
        var view = new BuilderView(model);
        var initialization = view.WarmupAsync().AsTask();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        view.Dispose();
        Assert.False(store.CancellationObserved.Task.IsCompleted);
        store.ReleaseLoad.TrySetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        using var replacement = new BuilderView(model);
        await replacement.WarmupAsync();
        Assert.Equal(1, store.LoadCount);
    }

    [AvaloniaFact]
    public async Task BuilderDispatcher_CancelledQueryDoesNotReturnDefaultOrHideCallbackFailure()
    {
        var dispatcher = new AvaloniaBuilderUiDispatcher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => dispatcher.InvokeAsync(() => ++calls, cancellation.Token)));
        Assert.Equal(0, calls);
        var callbackFailure = new OperationCanceledException(cancellation.Token);
        var observed = await Assert.ThrowsAsync<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationCallbackException>(
            () => Task.Run(() => dispatcher.InvokeAsync<int>(() => throw callbackFailure)));
        Assert.Same(callbackFailure, observed.InnerException);
        // Builder retains its callback/backend distinction even though the shared dispatcher
        // preserves ordinary failures for domain handlers such as subsession navigation.
        var ordinaryFailure = new IOException("Builder query callback failed.");
        observed = await Assert.ThrowsAsync<BuilderApp::Sunder.Package.Agent.Shared.Presentation.PresentationCallbackException>(
            () => Task.Run(() => dispatcher.InvokeAsync<int>(() => throw ordinaryFailure)));
        Assert.Same(ordinaryFailure, observed.InnerException);
    }

    private static BuilderViewModel CreateColdBuilderViewModel(IBuilderProjectStore store, BuilderProjectInspection? project = null)
    {
        var services = new ColdBuilderServices { Project = project };
        return new(new(services, store, new(new NoOpBackgroundTaskQueue()), services), new(store, services), new(),
            new AvaloniaBuilderUiDispatcher(), services, services, NullPackageSettingsNavigationService.Instance);
    }

    private sealed class BlockingBuilderProjectStore : IBuilderProjectStore
    {
        private int _loadCount;

        public int LoadCount => Volatile.Read(ref _loadCount);
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellationCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            LoadStarted.TrySetResult();
            try
            {
                await ReleaseLoad.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await ReleaseCancellationCleanup.Task;
                throw;
            }
            return [];
        }

        public Task SaveAsync(
            IReadOnlyList<BuilderProjectRecord> projects,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FailOnceBuilderProjectStore : IBuilderProjectStore
    {
        private int _loadCount;

        public int LoadCount => Volatile.Read(ref _loadCount);

        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _loadCount) == 1
                ? Task.FromException<IReadOnlyList<BuilderProjectRecord>>(
                    new InvalidOperationException("Injected Builder load failure."))
                : Task.FromResult<IReadOnlyList<BuilderProjectRecord>>([]);
        }

        public Task SaveAsync(
            IReadOnlyList<BuilderProjectRecord> projects,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

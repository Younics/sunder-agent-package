extern alias DockerApp;

using DockerApp::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;
using DockerPresentationService = DockerApp::Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Docker_Services.IPresentationService;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class DockerSettingsRaceTests
{
    [Fact]
    public async Task InitializeAsync_IsSharedAndNavigationRefreshUpdatesRowsInPlace()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());

        var first = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.InitializeAsync();

        AdaptiveEditorLifecycleContract.SharedInitialization(Adapt(viewModel), first, second);
        Assert.Same(first, second);
        Assert.False(runtime.Requests[1].Started.Task.IsCompleted);
        runtime.Requests[0].Response.TrySetResult(Response(
            new DockerImageDefinition("example:latest", DockerImageStatus.Ready, DateTimeOffset.UtcNow, null)));
        await Task.WhenAll(first, second);
        var row = Assert.Single(viewModel.Images);

        var update = viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:test",
            new Dictionary<string, string?>())).AsTask();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[1].Response.TrySetResult(Response(
            new DockerImageDefinition("example:latest", DockerImageStatus.Failed, null, "pull failed")));
        await update;

        Assert.Same(row, Assert.Single(viewModel.Images));
        Assert.Equal(DockerImageStatus.Failed, row.Status);
        Assert.Equal("pull failed", row.ErrorMessage);
    }

    [Fact]
    public async Task DelayedInitialization_DisablesMutationUntilHydrated()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());

        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.NewImageReference = "fresh:1.0";

        AdaptiveEditorLifecycleContract.HydrationBlocksMutation(Adapt(viewModel));
        Assert.False(viewModel.AddImageCommand.CanExecute(null));
        var refresh = viewModel.RefreshAllImagesCommand.ExecuteAsync(null);
        await refresh;
        Assert.False(runtime.Requests[1].Started.Task.IsCompleted);

        runtime.Requests[0].Response.TrySetResult(new DockerExecutionOperationResponse(
            "123",
            "/tmp/docker-hydrated",
            [Image("hydrated:1.0")],
            CatalogRevision: 1));
        await initialization;

        Assert.Equal("123", viewModel.TimeoutSeconds);
        Assert.Equal("/tmp/docker-hydrated", viewModel.DockerCliPath);
        Assert.Equal("hydrated:1.0", Assert.Single(viewModel.Images).ImageReference);
        Assert.True(viewModel.AddImageCommand.CanExecute(null));
    }

    [Fact]
    public async Task SuccessfulSave_PreventsOlderFullSnapshotFromApplying()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(Response(Image("initial:1.0")));
        await initialization;
        viewModel.TimeoutSeconds = "111";
        Assert.True(viewModel.ApplySelectedDockerCliPath("/tmp/docker-saved"));

        var staleSnapshot = viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:test",
            new Dictionary<string, string?>())).AsTask();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var save = viewModel.SaveSettingsCommand.ExecuteAsync(null);
        await runtime.Requests[2].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[2].Response.TrySetResult(new DockerExecutionOperationResponse(
            "111",
            "/tmp/docker-saved",
            []));
        await save;

        await staleSnapshot;
        runtime.Requests[1].Response.TrySetResult(new DockerExecutionOperationResponse(
            "999",
            "/tmp/docker-stale",
            [Image("stale:latest")],
            CatalogRevision: 99));

        Assert.Equal("111", viewModel.TimeoutSeconds);
        Assert.Equal("/tmp/docker-saved", viewModel.DockerCliPath);
        Assert.Equal("initial:1.0", Assert.Single(viewModel.Images).ImageReference);
        Assert.Equal("Docker execution settings saved.", viewModel.StatusText);
    }

    [Fact]
    public async Task NavigationRefresh_RejectsRegressiveCatalogSnapshot()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(new DockerExecutionOperationResponse(
            "300",
            string.Empty,
            [Image("current:1.0")],
            CatalogRevision: 5));
        await initialization;

        var refresh = viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:test",
            new Dictionary<string, string?>())).AsTask();
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[1].Response.TrySetResult(new DockerExecutionOperationResponse(
            "999",
            "/tmp/stale",
            [Image("stale:1.0")],
            CatalogRevision: 4));
        await refresh;

        Assert.Equal("current:1.0", Assert.Single(viewModel.Images).ImageReference);
        Assert.Equal("docker.protocol.invalid-response", viewModel.RuntimeErrorCode);
        Assert.DoesNotContain("regressive", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PassiveSnapshotSupersedingBusyRefresh_DoesNotLatchBusyState()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(Response(
            new DockerImageDefinition("alpha:latest", DockerImageStatus.Ready, null, null)));
        await initialization;

        var refresh = viewModel.RefreshSelectedImageCommand.ExecuteAsync(null);
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsBusy);
        var passive = viewModel.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:test",
            new Dictionary<string, string?>())).AsTask();
        await runtime.Requests[2].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(viewModel.IsBusy);
        runtime.Requests[2].Response.TrySetResult(Response(
            new DockerImageDefinition("alpha:latest", DockerImageStatus.Ready, null, null)));
        await passive;
        runtime.Requests[1].Response.TrySetResult(Response(
            new DockerImageDefinition("alpha:latest", DockerImageStatus.Failed, null, "stale")));
    }

    [Fact]
    public async Task OlderMutationsPreserveNewerImageSelectionTextAndSettingsFields()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(new DockerExecutionOperationResponse(
            "300",
            "/tmp/docker-initial",
            [
                Image("alpha:latest"),
                Image("beta:latest"),
            ],
            CatalogRevision: 1));
        await initialization;

        viewModel.NewImageReference = "added:latest";
        var add = viewModel.AddImageCommand.ExecuteAsync(null);
        await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.NewImageReference = "newer:text";
        viewModel.SelectedImage = viewModel.Images.Single(image => image.ImageReference == "beta:latest");
        runtime.Requests[1].Response.TrySetResult(new DockerExecutionOperationResponse(
            Images:
            [
                Image("alpha:latest"),
                Image("beta:latest"),
                Image("added:latest"),
            ],
            CatalogRevision: 2));
        await add;

        AdaptiveEditorLifecycleContract.CurrentSelectionWins(Adapt(viewModel), "beta:latest");
        Assert.Equal("newer:text", viewModel.NewImageReference);
        Assert.Equal("beta:latest", viewModel.SelectedImage?.ImageReference);

        viewModel.TimeoutSeconds = "111";
        Assert.True(viewModel.ApplySelectedDockerCliPath("/tmp/docker-old"));
        var save = viewModel.SaveSettingsCommand.ExecuteAsync(null);
        await runtime.Requests[2].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.TimeoutSeconds = "222";
        Assert.True(viewModel.ApplySelectedDockerCliPath("/tmp/docker-new"));
        runtime.Requests[2].Response.TrySetResult(new DockerExecutionOperationResponse(
            "111",
            "/tmp/docker-old",
            []));
        await save;

        AdaptiveEditorLifecycleContract.EditDuringSaveRemainsDirty(
            Adapt(
                viewModel,
                isDirty: () => viewModel.TimeoutSeconds == "222",
                draftValue: () => viewModel.TimeoutSeconds),
            "222");
        Assert.Equal("222", viewModel.TimeoutSeconds);
        Assert.Equal("/tmp/docker-new", viewModel.DockerCliPath);

        viewModel.SelectedImage = viewModel.Images.Single(image => image.ImageReference == "alpha:latest");
        var delete = viewModel.DeleteSelectedImageCommand.ExecuteAsync(null);
        await runtime.Requests[3].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedImage = viewModel.Images.Single(image => image.ImageReference == "added:latest");
        runtime.Requests[3].Response.TrySetResult(new DockerExecutionOperationResponse(
            Images:
            [
                Image("beta:latest"),
                Image("added:latest"),
            ],
            CatalogRevision: 3));
        await delete;

        AdaptiveEditorLifecycleContract.CurrentSelectionWins(Adapt(viewModel), "added:latest");
        Assert.Equal("added:latest", viewModel.SelectedImage?.ImageReference);
    }

    [Fact]
    public async Task ForegroundOperations_ContainTypedRuntimeFailuresAndClearBusyState()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Requests[0].Response.TrySetResult(Response(Image("configured:1.0")));
        await initialization;

        viewModel.NewImageReference = "new:1.0";
        var add = viewModel.AddImageCommand.ExecuteAsync(null);
        await FailRequestAsync(runtime.Requests[1]);
        await add;
        Assert.Equal("new:1.0", viewModel.NewImageReference);
        Assert.False(viewModel.IsBusy);

        var delete = viewModel.DeleteSelectedImageCommand.ExecuteAsync(null);
        await FailRequestAsync(runtime.Requests[2]);
        await delete;
        Assert.Equal("configured:1.0", viewModel.SelectedImage?.ImageReference);
        Assert.False(viewModel.IsBusy);

        var refresh = viewModel.RefreshSelectedImageCommand.ExecuteAsync(null);
        await FailRequestAsync(runtime.Requests[3]);
        await refresh;
        Assert.False(viewModel.IsBusy);

        var refreshAll = viewModel.RefreshAllImagesCommand.ExecuteAsync(null);
        await FailRequestAsync(runtime.Requests[4]);
        await refreshAll;
        Assert.False(viewModel.IsBusy);

        viewModel.TimeoutSeconds = "123";
        var save = viewModel.SaveSettingsCommand.ExecuteAsync(null);
        await FailRequestAsync(runtime.Requests[5]);
        await save;
        Assert.Equal("123", viewModel.TimeoutSeconds);
        Assert.False(viewModel.IsBusy);

        var test = viewModel.TestDockerCommand.ExecuteAsync(null);
        await FailRequestAsync(runtime.Requests[6]);
        await test;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsReady);
        Assert.Equal("docker.presentation.unavailable", viewModel.RuntimeErrorCode);
        Assert.Null(viewModel.RuntimeCorrelationId);
        Assert.Contains("docker.presentation.unavailable", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("host exception", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_TypedRuntimeFailureIsRetryableAndContained()
    {
        var runtime = new BlockingDockerRuntimeClient();
        using var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());

        await AdaptiveEditorLifecycleContract.RuntimeCycleIsExplicitAsync(Adapt(viewModel), async () =>
        {
            var first = viewModel.InitializeAsync();
            await FailRequestAsync(runtime.Requests[0]);
            await first;

            Assert.True(viewModel.IsError);
            Assert.True(viewModel.RetryInitializationCommand.CanExecute(null));
            Assert.Equal("docker.presentation.unavailable", viewModel.RuntimeErrorCode);

            var retry = viewModel.RetryInitializationCommand.ExecuteAsync(null);
            await runtime.Requests[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            runtime.Requests[1].Response.TrySetResult(Response(Image("recovered:1.0")));
            await retry;
        });

        Assert.True(viewModel.IsReady);
        Assert.Null(viewModel.RuntimeErrorCode);
        Assert.Equal("recovered:1.0", Assert.Single(viewModel.Images).ImageReference);
    }

    [Fact]
    public async Task DisposeCancelsNonCooperativeSnapshotWithoutLateApply()
    {
        var runtime = new BlockingDockerRuntimeClient();
        var viewModel = new DockerExecutionSettingsViewModel(
            runtime,
            new EmptyBackgroundTaskQueue());
        var initialization = viewModel.InitializeAsync();
        await runtime.Requests[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        var lateResponse = new DockerExecutionOperationResponse(
            "999",
            "/tmp/stale",
            [Image("stale:latest")],
            CatalogRevision: 99);
        await AdaptiveEditorLifecycleContract.RetiredWorkCannotPublishAsync(
            Adapt(viewModel, publicationCount: () => Volatile.Read(ref notifications)),
            viewModel.Dispose,
            () => runtime.Requests[0].Response.TrySetResult(lateResponse),
            initialization.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Empty(viewModel.Images);
        Assert.NotEqual("999", viewModel.TimeoutSeconds);
    }

    private static IAdaptiveEditorLifecycleAdapter Adapt(
        DockerExecutionSettingsViewModel viewModel,
        Func<bool>? isDirty = null,
        Func<string?>? draftValue = null,
        Func<int>? publicationCount = null)
        => new DelegateAdaptiveEditorLifecycleAdapter(
            "Docker",
            () => new AdaptiveEditorSnapshot(
                IsHydrating: !viewModel.IsDisposed && viewModel.IsLoading,
                CanMutate: !viewModel.IsDisposed && viewModel.CanMutate,
                IsBusy: !viewModel.IsDisposed && viewModel.IsBusy,
                IsDirty: isDirty?.Invoke() ?? false,
                viewModel.SelectedImage?.ImageReference,
                draftValue?.Invoke() ?? viewModel.NewImageReference,
                publicationCount?.Invoke() ?? 0,
                viewModel.IsError
                    ? AdaptiveEditorRuntimeState.Unavailable
                    : viewModel.IsReady
                        ? AdaptiveEditorRuntimeState.Available
                        : AdaptiveEditorRuntimeState.Unknown),
            viewModel);

    private static DockerExecutionOperationResponse Response(DockerImageDefinition image)
        => new("300", string.Empty, [image], CatalogRevision: 1);

    private static DockerImageDefinition Image(string imageReference)
        => new(imageReference, DockerImageStatus.Ready, null, null);

    private static async Task FailRequestAsync(PendingRequest request)
    {
        await request.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        request.Response.TrySetException(new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "docker.presentation.unavailable",
            "The Docker presentation provider is unavailable.")));
    }

    private sealed class BlockingDockerRuntimeClient : DockerPresentationService
    {
        private int _requestIndex;

        public PendingRequest[] Requests { get; } =
            Enumerable.Range(0, 12).Select(_ => new PendingRequest()).ToArray();

        public ValueTask<SunderContent> GetSettingsAsync(CancellationToken cancellationToken = default)
            => ExecuteCatalogAsync(cancellationToken);

        public ValueTask<SunderContent> AddImageAsync(string? imageReference, CancellationToken cancellationToken = default)
            => ExecuteCatalogAsync(cancellationToken);

        public ValueTask<SunderContent> DeleteImageAsync(string? imageReference, CancellationToken cancellationToken = default)
            => ExecuteCatalogAsync(cancellationToken);

        public ValueTask<SunderContent> RefreshImageAsync(string? imageReference, CancellationToken cancellationToken = default)
            => ExecuteCatalogAsync(cancellationToken);

        public ValueTask<SunderContent> RefreshImagesAsync(CancellationToken cancellationToken = default)
            => ExecuteCatalogAsync(cancellationToken);

        public ValueTask<SunderContent> PullImageAsync(string? imageReference, CancellationToken cancellationToken = default)
            => ExecuteCatalogAsync(cancellationToken);

        public ValueTask<DockerExecutionOperationResponse> SaveSettingsAsync(
            string? timeoutSeconds,
            string? dockerCliPath,
            CancellationToken cancellationToken = default)
            => ExecuteAsync(cancellationToken);

        public ValueTask<DockerExecutionOperationResponse> TestDockerAsync(CancellationToken cancellationToken = default)
            => ExecuteAsync(cancellationToken);

        private async ValueTask<SunderContent> ExecuteCatalogAsync(CancellationToken cancellationToken)
            => DockerPresentationTestContent.Create(await ExecuteAsync(cancellationToken));

        private async ValueTask<DockerExecutionOperationResponse> ExecuteAsync(
            CancellationToken cancellationToken)
        {
            var pending = Requests[Interlocked.Increment(ref _requestIndex) - 1];
            pending.Started.TrySetResult();
            return await pending.Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DockerExecutionOperationResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EmptyBackgroundTaskQueue : IBackgroundTaskQueue
    {
        public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged
        {
            add { }
            remove { }
        }

        public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
            => throw new NotSupportedException();

        public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null) => [];

        public bool Cancel(Guid processId) => false;
    }
}

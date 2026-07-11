using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderServiceTests
{
    [Fact]
    public async Task Persistence_OverlappingAutosaves_DebouncesToLatestGeneration()
    {
        var store = new RecordingProjectStore();
        await using var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMilliseconds(25));

        persistence.RequestSave([CreateRecord("one")]);
        persistence.RequestSave([CreateRecord("two")]);

        await store.WaitForSaveCountAsync(1);
        Assert.Equal("two", Assert.Single(Assert.Single(store.Saves)).Id);
        await Task.Delay(50);
        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task Persistence_StaleCompletion_DoesNotSuppressNewerGeneration()
    {
        var store = new RecordingProjectStore(blockFirstSave: true);
        await using var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMilliseconds(5));
        persistence.RequestSave([CreateRecord("one")]);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        persistence.RequestSave([CreateRecord("two")]);
        store.ReleaseFirstSave.TrySetResult();

        await store.WaitForSaveCountAsync(2);
        Assert.Equal(["one", "two"], store.Saves.Select(save => Assert.Single(save).Id).ToArray());
    }

    [Fact]
    public async Task Persistence_DisposalFlushesPendingSave()
    {
        var store = new RecordingProjectStore();
        var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMinutes(1));
        persistence.RequestSave([CreateRecord("pending")]);

        await persistence.DisposeAsync();

        Assert.Equal("pending", Assert.Single(Assert.Single(store.Saves)).Id);
    }

    [Fact]
    public async Task ViewModel_DisposalFlushesPendingRuntimeAutosave()
    {
        var root = CreateProjectFolder();
        try
        {
            var target = new TestExecutionTarget();
            var workspace = CreateWorkspace(root);
            var store = new RecordingProjectStore();
            var pathService = new BuilderPathService();
            var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMinutes(1));
            var viewModel = new BuilderViewModel(
                CreateApplicationService(workspace, target, store, pathService),
                new BuilderOperationQueue(new CapturingBackgroundProcessQueue()),
                persistence,
                pathService,
                new RecordingUiDispatcher());
            var project = new BuilderProjectViewModel(CreateInitializedRecord(root));
            viewModel.Workspaces.Add(workspace);
            viewModel.Projects.Add(project);
            viewModel.ActivateProject(project);

            project.Watch = false;
            await viewModel.DisposeAsync();

            Assert.False(Assert.Single(store.Saves[^1]).Watch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ViewModel_DisposalAfterFailedLoad_DoesNotFlushEmptyProjects()
    {
        var root = CreateProjectFolder();
        try
        {
            var store = new FailingLoadProjectStore(new InvalidDataException("Corrupt builder state."));
            var viewModel = CreateViewModelForLoadTest(root, store);

            await viewModel.InitializeAsync();
            await viewModel.DisposeAsync();

            Assert.Equal(0, store.SaveCount);
            Assert.Contains("Corrupt builder state.", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ViewModel_DisposalDuringCanceledLoad_DoesNotFlushEmptyProjects()
    {
        var root = CreateProjectFolder();
        try
        {
            var store = new CancelableLoadProjectStore();
            var viewModel = CreateViewModelForLoadTest(root, store);
            var initialization = viewModel.InitializeAsync();
            await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await viewModel.DisposeAsync();

            await initialization;
            Assert.Equal(0, store.SaveCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":2,\"projects\":[]}")]
    public async Task Store_CorruptOrFutureDocument_SurfacesAndRemainsUntouched(string persisted)
    {
        using var scope = RegressionTestPackageScope.Create();
        const string key = "builder.projects.v1";
        await scope.Context.Storage.State.SetValueAsync(key, persisted);
        var store = new BuilderProjectStore(scope.Context);

        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());

        Assert.Equal(persisted, await scope.Context.Storage.State.GetValueAsync(key));
    }

    [Fact]
    public void PathService_CalculatesNormalizedDevOutputAndRejectsTraversal()
    {
        var service = new BuilderPathService();
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-paths", Guid.NewGuid().ToString("N"));

        var relative = service.NormalizeDevPackageRelativePath("bin\\Release/net10.0/sunder-dev");
        var output = service.ResolveDevPackageFolder(root, relative);

        Assert.Equal("/bin/Release/net10.0/sunder-dev", relative);
        Assert.Equal(Path.Combine(root, "bin", "Release", "net10.0", "sunder-dev"), output);
        Assert.Equal(relative, service.TryResolveRelativeDevPackagePath(root, output));
        Assert.Throws<InvalidOperationException>(() => service.NormalizeDevPackageRelativePath("../outside"));
    }

    [Fact]
    public void PathService_ResolveContainedHostPath_RejectsDirectorySymlinkEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-containment", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        CreateDirectorySymlinkOrSkip(Path.Combine(workspace, "escape"), outside);

        try
        {
            var service = new BuilderPathService();

            Assert.Throws<InvalidOperationException>(() => service.ResolveContainedHostPath(
                Path.Combine(workspace, "escape", "project"),
                workspace));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateProject_RejectsHostProjectFolderSymlinkEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-validation", Guid.NewGuid().ToString("N"));
        var workspaceRoot = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Outside.csproj"), "<Project />");
        var projectLink = Path.Combine(workspaceRoot, "project");
        CreateDirectorySymlinkOrSkip(projectLink, outside);
        var workspace = CreateWorkspace(workspaceRoot);
        var service = CreateApplicationService(workspace, new TestExecutionTarget(), new RecordingProjectStore());

        try
        {
            var result = service.ValidateProject(
                CreateInitializedRecord(projectLink),
                [workspace],
                requireExistingFolder: true,
                requireInitializedPaths: true);

            Assert.False(result.IsValid);
            Assert.Contains("outside the selected workspace path", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OperationQueue_OverlappingRequests_AreMutuallyExclusive()
    {
        var backgroundProcesses = new CapturingBackgroundProcessQueue();
        var queue = new BuilderOperationQueue(backgroundProcesses);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;

        queue.Enqueue("first", async _ =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            firstEntered.TrySetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref active);
        });
        queue.Enqueue("second", _ =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            secondEntered.TrySetResult();
            Interlocked.Decrement(ref active);
            return Task.CompletedTask;
        });

        var first = backgroundProcesses.Requests[0].ExecuteAsync(CreateContext());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = backgroundProcesses.Requests[1].ExecuteAsync(CreateContext());
        await Task.Delay(30);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximumActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAndPublish_PropagateProcessCancellation(bool publish)
    {
        var root = CreateProjectFolder();
        try
        {
            var target = new TestExecutionTarget(blockProjectOperations: true);
            var workspace = CreateWorkspace(root);
            var service = CreateApplicationService(workspace, target, new RecordingProjectStore());
            using var cancellation = new CancellationTokenSource();
            var context = CreateContext(cancellation.Token);
            var project = CreateInitializedRecord(root);

            var operation = publish
                ? service.PublishProjectAsync(project, context)
                : service.BuildProjectAsync(project, context);
            await target.ProjectOperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
            Assert.Equal(publish ? "publish" : "build", target.LastProjectOperation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackgroundBuildCallback_UpdatesBoundStateThroughUiDispatcher()
    {
        var root = CreateProjectFolder();
        try
        {
            var target = new TestExecutionTarget();
            var workspace = CreateWorkspace(root);
            var store = new RecordingProjectStore();
            var backgroundProcesses = new CapturingBackgroundProcessQueue();
            var dispatcher = new RecordingUiDispatcher();
            var pathService = new BuilderPathService();
            var applicationService = CreateApplicationService(workspace, target, store, pathService);
            var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMilliseconds(5));
            var viewModel = new BuilderViewModel(
                applicationService,
                new BuilderOperationQueue(backgroundProcesses),
                persistence,
                pathService,
                dispatcher);
            var project = new BuilderProjectViewModel(CreateInitializedRecord(root));
            viewModel.Workspaces.Add(workspace);
            viewModel.Projects.Add(project);
            viewModel.ActivateProject(project);
            await viewModel.BuildSelectedProjectAsync();
            var request = Assert.Single(backgroundProcesses.Requests);
            dispatcher.Reset();
            var offDispatcherUpdates = 0;
            viewModel.PropertyChanged += (_, _) =>
            {
                if (!dispatcher.IsDispatching)
                {
                    Interlocked.Increment(ref offDispatcherUpdates);
                }
            };
            project.PropertyChanged += (_, _) =>
            {
                if (!dispatcher.IsDispatching)
                {
                    Interlocked.Increment(ref offDispatcherUpdates);
                }
            };

            await request.ExecuteAsync(CreateContext());

            Assert.Equal(0, offDispatcherUpdates);
            Assert.True(dispatcher.InvocationCount > 0);
            Assert.StartsWith("Build completed.", viewModel.StatusText, StringComparison.Ordinal);
            await viewModel.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BuilderProjectApplicationService CreateApplicationService(
        AgentWorkspaceRecord workspace,
        TestExecutionTarget target,
        IBuilderProjectStore store,
        BuilderPathService? pathService = null)
    {
        pathService ??= new BuilderPathService();
        return new BuilderProjectApplicationService(
            new BuilderSetupService(),
            new BuilderWorkspaceExecutionService(new TestExtensionCatalog(new TestWorkspaceExecutionResolver(workspace, target))),
            store,
            pathService,
            NullPackageSessionService.Instance);
    }

    private static BuilderViewModel CreateViewModelForLoadTest(string root, IBuilderProjectStore store)
    {
        var workspace = CreateWorkspace(root);
        var pathService = new BuilderPathService();
        return new BuilderViewModel(
            CreateApplicationService(workspace, new TestExecutionTarget(), store, pathService),
            new BuilderOperationQueue(new CapturingBackgroundProcessQueue()),
            new BuilderProjectPersistence(store, TimeSpan.FromMinutes(1)),
            pathService,
            new RecordingUiDispatcher());
    }

    private static BuilderProjectRecord CreateRecord(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new BuilderProjectRecord(id, id, $"local.{id}", "workspace.local", string.Empty, string.Empty, string.Empty, true, now, now);
    }

    private static BuilderProjectRecord CreateInitializedRecord(string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new BuilderProjectRecord(
            "project",
            "Builder Project",
            "local.builder.project",
            "workspace.local",
            root,
            root,
            Path.Combine(root, "bin", "Debug", "net10.0", "sunder-dev"),
            true,
            now,
            now)
        {
            WorkspacePathId = "workspace.path",
            DevPackageRelativePath = BuilderPathService.DefaultDevPackageRelativePath,
        };
    }

    private static string CreateProjectFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-services", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "BuilderProject.csproj"), "<Project />");
        return root;
    }

    private static AgentWorkspaceRecord CreateWorkspace(string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspaceRecord(
            "workspace.local",
            "Local workspace",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord("workspace.path", "workspace.local", root, true, 0, now, now)]);
    }

    private static BackgroundProcessContext CreateContext(CancellationToken cancellationToken = default)
        => new(cancellationToken, _ => { }, (_, _) => { }, _ => { });

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic link creation is unavailable: {exception.Message}");
        }
    }

    private sealed class RecordingProjectStore(bool blockFirstSave = false) : IBuilderProjectStore
    {
        private readonly object _syncRoot = new();
        private readonly bool _blockFirstSave = blockFirstSave;
        private readonly List<IReadOnlyList<BuilderProjectRecord>> _saves = [];
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IReadOnlyList<BuilderProjectRecord>> Saves
        {
            get
            {
                lock (_syncRoot)
                {
                    return _saves.ToArray();
                }
            }
        }

        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BuilderProjectRecord>>([]);

        public async Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
        {
            var saveNumber = Interlocked.Increment(ref _saveCount);
            lock (_syncRoot)
            {
                _saves.Add(projects.ToArray());
            }

            if (saveNumber == 1)
            {
                FirstSaveStarted.TrySetResult();
                if (_blockFirstSave)
                {
                    await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
                }
            }
        }

        public async Task WaitForSaveCountAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref _saveCount) < count)
            {
                if (DateTime.UtcNow >= timeout)
                {
                    throw new TimeoutException($"Timed out waiting for {count} saves.");
                }

                await Task.Delay(5);
            }
        }
    }

    private sealed class FailingLoadProjectStore(Exception error) : IBuilderProjectStore
    {
        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<BuilderProjectRecord>>(error);

        public Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelableLoadProjectStore : IBuilderProjectStore
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCount { get; private set; }

        public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingBackgroundProcessQueue : IBackgroundProcessQueue
    {
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
        {
            add { }
            remove { }
        }

        public List<BackgroundProcessRequest> Requests { get; } = [];

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
        {
            Requests.Add(request);
            return new BackgroundProcessSnapshot(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundProcessState.Queued,
                string.Empty,
                null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(),
                null,
                DateTimeOffset.UtcNow,
                null,
                null);
        }

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];

        public bool Cancel(Guid processId) => false;
    }

    private sealed class RecordingUiDispatcher : IBuilderUiDispatcher
    {
        private int _dispatchDepth;

        public bool IsDispatching => Volatile.Read(ref _dispatchDepth) > 0;

        public int InvocationCount { get; private set; }

        public bool CheckAccess() => IsDispatching;

        public void Post(Action action) => Invoke(action);

        public Task InvokeAsync(Action action)
        {
            Invoke(action);
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            Interlocked.Increment(ref _dispatchDepth);
            InvocationCount++;
            try
            {
                return Task.FromResult(action());
            }
            finally
            {
                Interlocked.Decrement(ref _dispatchDepth);
            }
        }

        public void Reset() => InvocationCount = 0;

        private void Invoke(Action action)
        {
            Interlocked.Increment(ref _dispatchDepth);
            InvocationCount++;
            try
            {
                action();
            }
            finally
            {
                Interlocked.Decrement(ref _dispatchDepth);
            }
        }
    }

    private sealed class TestExtensionCatalog(IAgentWorkspaceExecutionResolver resolver) : IPackageExtensionCatalog
    {
        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => resolver is TContract typedResolver
               && string.Equals(extensionPoint.Id, PackageExtensionPoints.WorkspaceExecutionResolvers.Id, StringComparison.Ordinal)
                ? [typedResolver]
                : [];
    }

    private sealed class TestWorkspaceExecutionResolver(AgentWorkspaceRecord workspace, TestExecutionTarget target)
        : IAgentWorkspaceExecutionResolver
    {
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [workspace];

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var binding = new AgentWorkspaceBindingRecord(
                "binding.local",
                workspace.WorkspaceId,
                PackageExtensionPoints.ExecutionTargets.Id,
                "local",
                "primary",
                true,
                0,
                now,
                now);
            var scope = new AgentExecutionScopeDescriptor("Local", [workspace.Paths[0].HostPath], workspace.Paths[0].HostPath);
            return ValueTask.FromResult(new AgentWorkspaceExecutionResolution(
                workspace,
                binding,
                target.Descriptor,
                scope,
                target));
        }
    }

    private sealed class TestExecutionTarget(bool blockProjectOperations = false) : IAgentProcessExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "local",
            "local",
            "Local",
            null,
            SupportsShell: true,
            SupportsFiles: true,
            SupportsSearch: false);

        public TaskCompletionSource ProjectOperationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastProjectOperation { get; private set; }

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready"));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "Shell", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Shell"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, string.Empty));

        public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
            AgentExecutionTargetContext context,
            AgentProcessCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            LastProjectOperation = request.Arguments.FirstOrDefault();
            ProjectOperationStarted.TrySetResult();
            if (blockProjectOperations && LastProjectOperation is "build" or "publish")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new AgentShellCommandResult(0, string.Empty);
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileReadResult(request.Path, string.Empty));

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Written"));

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Deleted"));
    }
}

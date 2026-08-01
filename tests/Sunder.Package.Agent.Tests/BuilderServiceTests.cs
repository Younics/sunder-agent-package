using System.Text.Json;
using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
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
    public async Task Persistence_ConcurrentDisposalSharesFinalFlush()
    {
        var store = new RecordingProjectStore(blockFirstSave: true);
        var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMinutes(1));
        persistence.RequestSave([CreateRecord("pending")]);

        var firstDisposal = persistence.DisposeAsync().AsTask();
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondDisposal = persistence.DisposeAsync().AsTask();

        Assert.False(firstDisposal.IsCompleted);
        Assert.False(secondDisposal.IsCompleted);
        store.ReleaseFirstSave.TrySetResult();
        await Task.WhenAll(firstDisposal, secondDisposal);

        Assert.Equal("pending", Assert.Single(Assert.Single(store.Saves)).Id);
    }

    [Fact]
    public async Task Persistence_DisposalWaitsForSaveAlreadyInProgress()
    {
        var store = new RecordingProjectStore(blockFirstSave: true);
        var persistence = new BuilderProjectPersistence(store, TimeSpan.FromMinutes(1));
        var save = persistence.SaveNowAsync([CreateRecord("saving")]);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = persistence.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        store.ReleaseFirstSave.TrySetResult();
        await Task.WhenAll(save, disposal);

        Assert.Equal("saving", Assert.Single(Assert.Single(store.Saves)).Id);
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
    public async Task RuntimeBridge_RoutesWorkspaceResolutionAndProcessExecution()
    {
        var workspace = CreateWorkspace(Path.GetTempPath());
        var target = new TestExecutionTarget(output: "Runtime output");
        var runtime = new LoopbackBuilderRuntimeClient(CreateBuilderRuntimeHandler(workspace, target));
        var service = new BuilderWorkspaceExecutionService(runtime);

        Assert.Equal(
            workspace.WorkspaceId,
            Assert.Single(await service.ListWorkspacesAsync()).WorkspaceId);
        var execution = await service.ResolveAsync(workspace.WorkspaceId);
        var result = await execution.RunProcessAsync("dotnet", ["build"]);

        Assert.Equal("Runtime output", result.CombinedOutput);
        Assert.Equal("build", target.LastProjectOperation);
        Assert.Equal(
            [
                "ListWorkspaces",
                "ResolveWorkspace",
                "ExecuteProcess",
            ],
            runtime.RequestKinds);
    }

    [Fact]
    public async Task RuntimeBridge_TruncatesProcessOutputBeforeTransport()
    {
        var workspace = CreateWorkspace(Path.GetTempPath());
        var target = new TestExecutionTarget(output: new string('x', (512 * 1024) + 1));
        var runtime = new LoopbackBuilderRuntimeClient(CreateBuilderRuntimeHandler(workspace, target));
        var execution = await new BuilderWorkspaceExecutionService(runtime).ResolveAsync(workspace.WorkspaceId);

        var result = await execution.RunShellAsync("dotnet build");

        Assert.Equal(512 * 1024, result.CombinedOutput.Length);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task RuntimeBridge_FitsWorkspaceListBelowResponseTransportLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var longPath = "/" + new string('x', 2047);
        var workspaces = Enumerable.Range(0, 100)
            .Select(index => new AgentWorkspaceRecord(
                $"workspace-{index}",
                $"Workspace {index}",
                null,
                now,
                now,
                Enumerable.Range(0, 64)
                    .Select(pathIndex => new AgentWorkspacePathRecord(
                        $"path-{index}-{pathIndex}",
                        $"workspace-{index}",
                        longPath,
                        pathIndex == 0,
                        pathIndex,
                        now,
                        now))
                    .ToArray()))
            .ToArray();
        var runtime = new LoopbackBuilderRuntimeClient(CreateBuilderRuntimeHandler(
            new ListOnlyWorkspaceExecutionResolver(workspaces)));

        var projected = await new BuilderWorkspaceExecutionService(runtime).ListWorkspacesAsync();

        Assert.NotEmpty(projected);
        Assert.True(projected.Count < workspaces.Length);
        Assert.True(runtime.MaximumResponseBytes < (4 * 1024 * 1024) - (64 * 1024));
    }

    [Fact]
    public async Task WorkspaceList_RunsSynchronousResolverOffCallingThreadAndSupportsCallerCancellation()
    {
        var resolver = new BlockingWorkspaceExecutionResolver();
        var service = new BuilderWorkspaceExecutionService(resolver);
        using var cancellation = new CancellationTokenSource();
        using var allowCallerToExit = new ManualResetEventSlim();
        var callReturned = new TaskCompletionSource<Task<IReadOnlyList<AgentWorkspaceRecord>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThread = new Thread(() =>
        {
            try
            {
                callReturned.TrySetResult(service.ListWorkspacesAsync(cancellation.Token));
            }
            catch (Exception exception)
            {
                callReturned.TrySetException(exception);
            }
            finally
            {
                allowCallerToExit.Wait();
            }
        })
        {
            IsBackground = true,
            Name = nameof(WorkspaceList_RunsSynchronousResolverOffCallingThreadAndSupportsCallerCancellation),
        };

        callerThread.Start();
        try
        {
            await Task.WhenAll(resolver.Started.Task, callReturned.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var listing = await callReturned.Task;
            var invocationThread = Assert.IsType<Thread>(resolver.InvocationThread);
            Assert.NotSame(callerThread, invocationThread);
            Assert.False(resolver.Release.Task.IsCompleted);
            Assert.False(listing.IsCompleted);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(resolver.Release.Task.IsCompleted);
        }
        finally
        {
            resolver.Release.TrySetResult();
            allowCallerToExit.Set();
            Assert.True(callerThread.Join(TimeSpan.FromSeconds(2)));
            if (resolver.Started.Task.IsCompletedSuccessfully)
            {
                await resolver.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
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
            new BuilderWorkspaceExecutionService(new TestWorkspaceExecutionResolver(workspace, target)),
            store,
            pathService);
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
        return new BuilderProjectRecord(id, id, $"local.{id}", "workspace.local", string.Empty, string.Empty, now, now);
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
            now,
            now)
        {
            WorkspacePathId = "workspace.path",
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

    private static object CreateBuilderRuntimeHandler(
        AgentWorkspaceRecord workspace,
        TestExecutionTarget target)
        => CreateBuilderRuntimeHandler(new TestWorkspaceExecutionResolver(workspace, target));

    private static object CreateBuilderRuntimeHandler(IAgentWorkspaceExecutionResolver resolver)
    {
        var handlerType = typeof(BuilderWorkspaceExecutionService).Assembly.GetType(
            "Sunder.Package.Agent.Builder.BuilderRuntimeHandler",
            throwOnError: true)!;
        return Activator.CreateInstance(
            handlerType,
            new TestExtensionCatalog(resolver))!;
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

    private sealed class TestExtensionCatalog : RegressionTestExtensionCatalog
    {
        public TestExtensionCatalog(IAgentWorkspaceExecutionResolver resolver)
        {
            if (resolver is TestWorkspaceExecutionResolver testResolver)
            {
                AddProvider(AgentRpcServices.ExecutionTargets, testResolver.Target);
                testResolver.BindTargetReference(GetRequiredReference(AgentRpcServices.ExecutionTargets));
            }
            AddProvider(AgentRpcServices.WorkspaceExecutionResolvers, resolver);
        }
    }

    private sealed class TestWorkspaceExecutionResolver : IAgentWorkspaceExecutionResolver
    {
        private readonly AgentWorkspaceRecord _workspace;
        private readonly TestExecutionTarget _target;
        private readonly RegressionTestExtensionCatalog _targetCatalog = new();
        private AgentRpcReference<IAgentExecutionTarget> _targetReference;

        public TestWorkspaceExecutionResolver(AgentWorkspaceRecord workspace, TestExecutionTarget target)
        {
            _workspace = workspace;
            _target = target;
            _targetCatalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
            _targetReference = _targetCatalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        }

        public TestExecutionTarget Target => _target;

        public void BindTargetReference(AgentRpcReference<IAgentExecutionTarget> targetReference)
            => _targetReference = targetReference;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [_workspace];

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var binding = new AgentWorkspaceBindingRecord(
                "binding.local",
                _workspace.WorkspaceId,
                AgentRpcContractIds.ExecutionTarget,
                "local",
                "primary",
                true,
                0,
                now,
                now);
            var scope = new AgentExecutionScopeDescriptor(
                "Local",
                [_workspace.Paths[0].HostPath],
                _workspace.Paths[0].HostPath);
            return ValueTask.FromResult(new AgentWorkspaceExecutionResolution(
                _workspace,
                binding,
                _target.Descriptor,
                scope,
                _target)
            {
                ExecutionTargetReference = _targetReference,
                ExecutionTargetHandle = _targetReference.ToHandle(),
            });
        }
    }

    private sealed class ListOnlyWorkspaceExecutionResolver(
        IReadOnlyList<AgentWorkspaceRecord> workspaces) : IAgentWorkspaceExecutionResolver
    {
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => workspaces;

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentWorkspaceExecutionResolution>(new NotSupportedException());
    }

    private sealed class BlockingWorkspaceExecutionResolver : IAgentWorkspaceExecutionResolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Thread? InvocationThread { get; private set; }

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
        {
            InvocationThread = Thread.CurrentThread;
            Started.TrySetResult();
            try
            {
                Release.Task.GetAwaiter().GetResult();
                return [];
            }
            finally
            {
                Completed.TrySetResult();
            }
        }

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentWorkspaceExecutionResolution>(new NotSupportedException());
    }

    private sealed class TestExecutionTarget(
        bool blockProjectOperations = false,
        string output = "") : IAgentProcessExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "local",
            "local",
            "Local",
            null,
            SupportsShell: true,
            SupportsFiles: true);

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
            => ValueTask.FromResult(new AgentShellCommandResult(0, output));

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

            return new AgentShellCommandResult(0, output);
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

    private sealed class LoopbackBuilderRuntimeClient(object handler)
        : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public List<string> RequestKinds { get; } = [];

        public int MaximumResponseBytes { get; private set; }

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            Assert.Equal("agent.builder.execute.v1", operation.OperationId);
            RequestKinds.Add(request.GetType().GetProperty("Kind")!.GetValue(request)!.ToString()!);
            var invocation = handler.GetType().GetMethod("HandleAsync")!.Invoke(
                handler,
                [request, cancellationToken])!;
            var task = (Task)invocation.GetType().GetMethod("AsTask")!.Invoke(invocation, null)!;
            await task;
            var response = (TResponse)task.GetType().GetProperty("Result")!.GetValue(task)!;
            MaximumResponseBytes = Math.Max(
                MaximumResponseBytes,
                JsonSerializer.SerializeToUtf8Bytes(response, response.GetType()).Length);
            return response;
        }

        public IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
            => throw new NotSupportedException();
    }
}

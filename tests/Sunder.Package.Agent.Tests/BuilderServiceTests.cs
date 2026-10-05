extern alias BuilderWorker;
extern alias BuilderApp;

using System.Text.Json;
using BuilderApp::Sunder.Package.Agent.Builder;
using BuilderWorker::Sunder.Package.Agent.Builder.Rpc;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderServiceTests
{
    [Fact]
    public void RpcPayloadLimits_RejectInconsistentRegisteredContentMetadata()
    {
        var bytes = BuilderRpcPayloadLimits.SerializeResponse(new BuilderRpcResponse());
        var deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        var reference = new SunderRpcContentReference(
            "builder-result",
            bytes.LongLength,
            new string('0', 64),
            "application/json",
            "builder-result.json",
            deadlineUtc,
            SunderRpcContentRepeatability.SingleUse);

        Assert.Throws<InvalidDataException>(() =>
            BuilderRpcPayloadLimits.ValidateRegisteredContent(reference, bytes, deadlineUtc));
    }

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

    [Fact]
    public async Task ViewModel_DisposalAfterUnchangedLoad_DoesNotRewriteProjects()
    {
        var root = CreateProjectFolder();
        try
        {
            var store = new RecordingProjectStore();
            var viewModel = CreateViewModelForLoadTest(root, store);

            await viewModel.InitializeAsync();
            await viewModel.DisposeAsync();

            Assert.Empty(store.Saves);
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

    [FileSystemFact(
        TestFileSystemCapability.DirectorySymbolicLinks,
        "Builder host-path containment requires directory symbolic links.")]
    public void PathService_ResolveContainedHostPath_RejectsDirectorySymlinkEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-containment", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(workspace, "escape"), outside);

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

    [FileSystemFact(
        TestFileSystemCapability.DirectorySymbolicLinks,
        "Builder project validation requires directory symbolic links.")]
    public void ValidateProject_RejectsHostProjectFolderSymlinkEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-validation", Guid.NewGuid().ToString("N"));
        var workspaceRoot = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Outside.csproj"), "<Project />");
        var projectLink = Path.Combine(workspaceRoot, "project");
        Directory.CreateSymbolicLink(projectLink, outside);
        var workspace = CreateWorkspace(workspaceRoot);
        var service = CreateApplicationService(workspace, new TestExecutionTarget(), new RecordingProjectStore());

        try
        {
            var result = service.ValidateProject(
                CreateInitializedRecord(projectLink),
                [AgentRoleBoundary.As<BuilderApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace)],
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
        using var runtime = CreateBuilderRpcClient(workspace, target);
        var service = new BuilderWorkspaceExecutionService(new BuilderRpcGateway(runtime));

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
    public async Task RunProcess_RejectsShellOnlyTargetWithoutSynthesizingCommandText()
    {
        var workspace = CreateWorkspace(Path.GetTempPath());
        var target = new ShellOnlyExecutionTarget();
        using var runtime = new LoopbackBuilderRpcClient(
            new ShellOnlyWorkspaceExecutionResolver(workspace, target));
        var execution = await new BuilderWorkspaceExecutionService(new BuilderRpcGateway(runtime))
            .ResolveAsync(workspace.WorkspaceId);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => execution.RunProcessAsync(
            "tool & attacker",
            ["value; Remove-Item -Recurse *", "value & del /s *"])
            .AsTask());

        Assert.Contains("structured process-execution facet", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, target.ShellInvocations);
        Assert.Equal(["ResolveWorkspace"], runtime.RequestKinds);
    }

    [Fact]
    public async Task RuntimeBridge_TruncatesProcessOutputBeforeTransport()
    {
        var workspace = CreateWorkspace(Path.GetTempPath());
        var target = new TestExecutionTarget(output: new string('x', (512 * 1024) + 1));
        using var runtime = CreateBuilderRpcClient(workspace, target);
        var execution = await new BuilderWorkspaceExecutionService(new BuilderRpcGateway(runtime)).ResolveAsync(workspace.WorkspaceId);

        var result = await execution.RunShellAsync("dotnet build");

        Assert.Equal(512 * 1024, result.CombinedOutput.Length);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task RuntimeBridge_ReturnsCompleteWorkspaceListWithoutSilentProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var longDisplayName = new string('n', 257);
        var longDescription = new string('d', 1025);
        var longPath = "/" + new string('x', 2048);
        var workspaces = Enumerable.Range(0, 101)
            .Select(index => new AgentWorkspaceRecord(
                $"workspace-{index}",
                index == 0 ? longDisplayName : $"Workspace {index}",
                index == 0 ? longDescription : null,
                now,
                now,
                Enumerable.Range(0, 65)
                    .Select(pathIndex => new AgentWorkspacePathRecord(
                        $"path-{index}-{pathIndex}",
                        $"workspace-{index}",
                        index == 0 && pathIndex == 0 ? longPath : $"/workspace/{index}/{pathIndex}",
                        pathIndex == 0,
                        pathIndex,
                        now,
                        now))
                    .ToArray(),
                [new AgentWorkspaceDocumentRecord(
                    $"document-{index}",
                    $"workspace-{index}",
                    $"instructions-{index}.md",
                    0,
                    now,
                    now)]))
            .ToArray();
        using var runtime = new LoopbackBuilderRpcClient(
            new ListOnlyWorkspaceExecutionResolver(workspaces));

        var projected = await new BuilderWorkspaceExecutionService(new BuilderRpcGateway(runtime)).ListWorkspacesAsync();

        Assert.Equal(workspaces.Length, projected.Count);
        var first = projected[0];
        Assert.Equal(longDisplayName, first.DisplayName);
        Assert.Equal(longDescription, first.Description);
        Assert.Equal(65, first.Paths.Count);
        Assert.Equal(longPath, first.Paths[0].HostPath);
        Assert.Single(first.Documents);
        Assert.True(runtime.MaximumResponseBytes > 900_000);
    }

    [Fact]
    public async Task WorkspaceList_RunsSynchronousResolverOffCallingThreadAndSupportsCallerCancellation()
    {
        var resolver = new BlockingWorkspaceExecutionResolver();
        using var runtime = new LoopbackBuilderRpcClient(resolver);
        var service = new BuilderWorkspaceExecutionService(new BuilderRpcGateway(runtime));
        using var cancellation = new CancellationTokenSource();
        using var allowCallerToExit = new ManualResetEventSlim();
        var callReturned = new TaskCompletionSource<Task<IReadOnlyList<BuilderApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>>>(
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
    public async Task WorkspaceList_PropagatesCallerCancellationThroughResolverRpcRoute()
    {
        var resolver = new CancellationAwareWorkspaceExecutionResolver();
        using var runtime = new LoopbackBuilderRpcClient(resolver);
        var service = new BuilderWorkspaceExecutionService(new BuilderRpcGateway(runtime));
        using var cancellation = new CancellationTokenSource();

        var listing = service.ListWorkspacesAsync(cancellation.Token);
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await resolver.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
    [InlineData(null)]
    [InlineData("restore")]
    [InlineData("accept")]
    public async Task InitializeProject_UsesOneRootAndExplicitInitialContractWorkflow(string? failStep)
    {
        var root = CreateProjectFolder();
        try
        {
            var target = new TestExecutionTarget(prerequisitesInstalled: true, failInitializationStep: failStep);
            var workspace = CreateWorkspace(root);
            var service = CreateApplicationService(workspace, target, new RecordingProjectStore());
            var project = CreateRecord("sample") with { WorkspacePathId = "workspace.path" };
            var initialize = service.InitializeProjectAsync(project,
                AgentRoleBoundary.As<BuilderApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspacePathRecord>(Assert.Single(workspace.Paths)), CreateContext());
            if (failStep is null)
            {
                var initialized = await initialize;
                Assert.EndsWith("sample", initialized.Project.ExecutionProjectFolder, StringComparison.Ordinal);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => initialize);
                Assert.Contains(failStep + " failed", error.Message, StringComparison.Ordinal);
            }
            var create = Assert.Single(target.Requests, request => request.Arguments.Count > 1 && request.Arguments[1] == "sunder-package");
            Assert.DoesNotContain("--createInPlace", create.Arguments);
            Assert.Single(target.Requests, request => request.Arguments[0] == "restore");
            var accepts = target.Requests.Where(request => request.Arguments.Contains("-t:AcceptSunderContracts")).ToArray();
            if (failStep == "restore") Assert.Empty(accepts);
            else Assert.Equal(new[] { "msbuild", "-t:AcceptSunderContracts", "-p:SunderContractKey=sample", "-p:SunderContractNextVersion=2.0.0" }, Assert.Single(accepts).Arguments);
        }
        finally { Directory.Delete(root, recursive: true); }
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
            Assert.Equal(publish ? "msbuild" : "build", target.LastProjectOperation);
            if (publish)
                Assert.Equal(new[] { "msbuild", "-t:CheckSunderContracts;FreezeSunderContracts;PackSunderPackage", "-p:Configuration=Release" }, target.LastProjectArguments);
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
            viewModel.Workspaces.Add(AgentRoleBoundary.As<BuilderApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace));
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
            new BuilderWorkspaceExecutionService(new BuilderRpcGateway(CreateBuilderRpcClient(workspace, target))),
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

    private static LoopbackBuilderRpcClient CreateBuilderRpcClient(
        AgentWorkspaceRecord workspace,
        TestExecutionTarget target)
        => new(new TestWorkspaceExecutionResolver(workspace, target));

    private static BackgroundProcessContext CreateContext(CancellationToken cancellationToken = default)
        => new(cancellationToken, _ => { }, (_, _) => { }, _ => { });

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
            if (resolver is ITestWorkspaceExecutionResolverBinding testResolver)
            {
                AddProvider(RegressionTestBindings.ExecutionTargets, testResolver.Target);
                testResolver.BindTargetReference(GetRequiredReference(RegressionTestBindings.ExecutionTargets));
            }
            AddProvider(AgentRpcContractIds.WorkspaceExecutionResolver, resolver, "sunder.package.agent");
        }
    }

    private sealed class TestWorkspaceExecutionResolver :
        IAgentWorkspaceExecutionResolver,
        ITestWorkspaceExecutionResolverBinding
    {
        private readonly AgentWorkspaceRecord _workspace;
        private readonly TestExecutionTarget _target;
        private readonly RegressionTestExtensionCatalog _targetCatalog = new();
        private AgentRpcReference<IAgentExecutionTarget> _targetReference;

        public TestWorkspaceExecutionResolver(AgentWorkspaceRecord workspace, TestExecutionTarget target)
        {
            _workspace = workspace;
            _target = target;
            _targetCatalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
            _targetReference = _targetCatalog.GetRequiredReference(RegressionTestBindings.ExecutionTargets);
        }

        public TestExecutionTarget Target => _target;

        IAgentExecutionTarget ITestWorkspaceExecutionResolverBinding.Target => _target;

        public void BindTargetReference(AgentRpcReference<IAgentExecutionTarget> targetReference)
            => _targetReference = targetReference;

        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([_workspace]);

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
        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(workspaces);

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

        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
        {
            InvocationThread = Thread.CurrentThread;
            Started.TrySetResult();
            try
            {
                Release.Task.GetAwaiter().GetResult();
                return ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([]);
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

    private sealed class CancellationAwareWorkspaceExecutionResolver : IAgentWorkspaceExecutionResolver
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentWorkspaceExecutionResolution>(new NotSupportedException());
    }

    private sealed class TestExecutionTarget(
        bool blockProjectOperations = false,
        string output = "",
        bool prerequisitesInstalled = false,
        string? failInitializationStep = null) : IAgentProcessExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "local",
            "local",
            "Local",
            null,
            SupportsShell: true,
            SupportsFiles: true)
        {
            Facets = [AgentExecutionFacetIds.ProcessExecution],
        };

        public TaskCompletionSource ProjectOperationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastProjectOperation { get; private set; }
        public IReadOnlyList<string>? LastProjectArguments { get; private set; }
        public List<AgentProcessCommandRequest> Requests { get; } = [];

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
            Requests.Add(request);
            if (prerequisitesInstalled && request.Arguments.SequenceEqual(["--list-sdks"]))
                return new AgentShellCommandResult(0, "10.0.100 [test-sdk]");
            if (prerequisitesInstalled && request.Arguments.SequenceEqual(["new", "list", "sunder-package"]))
                return new AgentShellCommandResult(0, "sunder-package");
            if (failInitializationStep == "restore" && request.Arguments[0] == "restore")
                return new AgentShellCommandResult(1, "restore failed");
            if (failInitializationStep == "accept" && request.Arguments.Contains("-t:AcceptSunderContracts"))
                return new AgentShellCommandResult(1, "accept failed");
            LastProjectOperation = request.Arguments.FirstOrDefault();
            LastProjectArguments = request.Arguments.ToArray();
            ProjectOperationStarted.TrySetResult();
            if (blockProjectOperations && LastProjectOperation is "build" or "msbuild")
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

    private sealed class ShellOnlyWorkspaceExecutionResolver(
        AgentWorkspaceRecord workspace,
        ShellOnlyExecutionTarget target) :
        IAgentWorkspaceExecutionResolver,
        ITestWorkspaceExecutionResolverBinding
    {
        private AgentRpcReference<IAgentExecutionTarget>? _targetReference;

        IAgentExecutionTarget ITestWorkspaceExecutionResolverBinding.Target => target;

        public void BindTargetReference(AgentRpcReference<IAgentExecutionTarget> targetReference)
            => _targetReference = targetReference;

        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([workspace]);

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var binding = new AgentWorkspaceBindingRecord(
                "binding.shell-only",
                workspace.WorkspaceId,
                AgentRpcContractIds.ExecutionTarget,
                "shell-only",
                "primary",
                true,
                0,
                now,
                now);
            return ValueTask.FromResult(new AgentWorkspaceExecutionResolution(
                workspace,
                binding,
                target.Descriptor,
                new AgentExecutionScopeDescriptor(
                    "Shell only",
                    [workspace.Paths[0].HostPath],
                    workspace.Paths[0].HostPath),
                target)
            {
                ExecutionTargetReference = _targetReference
                    ?? throw new InvalidOperationException("Shell-only target was not bound."),
                ExecutionTargetHandle = _targetReference.ToHandle(),
            });
        }
    }

    private sealed class ShellOnlyExecutionTarget : IAgentExecutionTarget
    {
        private int _shellInvocations;

        public int ShellInvocations => Volatile.Read(ref _shellInvocations);

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "shell-only",
            "shell-only",
            "Shell only",
            null,
            SupportsShell: true,
            SupportsFiles: false);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                "shell-only",
                "shell-only",
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready"));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor(
                "powershell",
                "PowerShell",
                "pwsh",
                AgentShellSyntaxKinds.PowerShell,
                "PowerShell"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _shellInvocations);
            return ValueTask.FromResult(new AgentShellCommandResult(0, string.Empty));
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    internal sealed class LoopbackBuilderRpcClient : ISunderRpcClient, IDisposable
    {
        private readonly RegressionTestExtensionCatalog _catalog = new();
        private readonly BuilderExecutionBindings _bindings;
        private readonly RegressionTestRpcState _state;
        private readonly RegressionTestRpcClient _client;

        public LoopbackBuilderRpcClient(IAgentWorkspaceExecutionResolver resolver)
        {
            _client = (RegressionTestRpcClient)_catalog.RpcForCaller("sunder.package.agent.builder");
            _state = _client.State;
            if (resolver is ITestWorkspaceExecutionResolverBinding testResolver)
            {
                _catalog.AddProvider(RegressionTestBindings.ExecutionTargets, testResolver.Target, "test.execution.target");
                testResolver.BindTargetReference(
                    _catalog.GetRequiredReference(RegressionTestBindings.ExecutionTargets));
            }
            _catalog.AddProvider(AgentRpcContractIds.WorkspaceExecutionResolver, resolver, "sunder.package.agent");
            _bindings = new BuilderExecutionBindings(_client);
            _bindings.StartAsync().GetAwaiter().GetResult();
            var provider = new BuilderWorker::Sunder.Package.Agent.Builder.BuilderRuntimeService(_bindings);
            var recordingProvider = new RecordingBuilderRuntimeHandler(this, provider.CreateSunderRpcProviderRegistration().Handler);
            _state.AddGenerated(
                "sunder.package.agent.builder",
                recordingProvider,
                BuilderWorker::Sunder.Package.Agent.Builder.BuilderRuntimeService.DeclareSunderRpcProvider(recordingProvider),
                NoopDisposable.Instance,
                "builder.runtime");
        }

        public List<string> RequestKinds { get; } = [];

        public int MaximumResponseBytes { get; private set; }

        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => _client.CreateCallScopeAsync(options, cancellationToken);

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => _client.GetProviderAsync(endpoint, cancellationToken);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => _client.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => _client.DiscoverAsync(contractId, cancellationToken);

        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            CancellationToken cancellationToken = default)
            => _client.WatchAsync(afterRevision, afterSequence, cancellationToken);

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => _client.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);

        public IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => _client.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);

        public void Dispose()
        {
            try { Task.Run(async () => await _bindings.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult(); }
            finally { _catalog.Dispose(); }
        }

        private sealed class RecordingBuilderRuntimeHandler(
            LoopbackBuilderRpcClient owner,
            ISunderRpcServiceHandler provider) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
        {
            public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
                => ((ISunderRpcDeclaredErrorMetadata)provider).DeclaredErrors;

            public async ValueTask<JsonElement> InvokeUnaryAsync(
                SunderRpcInvocationContext context,
                string serviceId,
                string methodId,
                JsonElement request,
                CancellationToken cancellationToken = default)
            {
                owner.RequestKinds.Add(Enum.Parse<BuilderRpcOperationKind>(request.GetProperty("kind").GetString()!.Replace("-", "", StringComparison.Ordinal), ignoreCase: true).ToString());
                var response = await provider.InvokeUnaryAsync(context, serviceId, methodId, request, cancellationToken).ConfigureAwait(false);
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new SunderRpcJsonConverterFactory() } };
                var content = response.GetProperty("content").Deserialize<SunderRpcContentReference>(options)!;
                owner.MaximumResponseBytes = Math.Max(
                    owner.MaximumResponseBytes,
                    checked((int)content.Length));
                return response;
            }

            public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId,
                string methodId, JsonElement request, CancellationToken cancellationToken = default)
                => provider.InvokeServerStreamAsync(context, serviceId, methodId, request, cancellationToken);
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}

internal interface ITestWorkspaceExecutionResolverBinding
{
    IAgentExecutionTarget Target { get; }

    void BindTargetReference(AgentRpcReference<IAgentExecutionTarget> targetReference);
}

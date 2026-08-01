using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderViewModelTests
{
    [Fact]
    public async Task InitializeAsync_ConcurrentCallsShareOneLoad()
    {
        var store = new BlockingBuilderProjectStore();
        var viewModel = CreateViewModel(store: store);

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(first, second);
        Assert.Equal(1, store.LoadCount);

        store.ReleaseLoad.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task InitializeAsync_LoadFailureIsObservedAndPresented()
    {
        var store = new FailingBuilderProjectStore();
        var viewModel = CreateViewModel(store: store);

        await viewModel.InitializeAsync();

        Assert.Contains("initialization failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Injected builder load failure", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("Injected builder load failure", viewModel.RuntimeLogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateProject_DuringBlockedInitializationPreservesDraftAndSelection()
    {
        var store = new BlockingBuilderProjectStore();
        await using var viewModel = CreateViewModel(store: store);
        var initialization = viewModel.InitializeAsync();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.CreateProjectAsync();
        var draft = Assert.IsType<BuilderProjectViewModel>(viewModel.SelectedProject);
        draft.DisplayName = "Draft created during initialization";

        await store.SecondLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        store.ReleaseLoad.TrySetResult();
        await initialization;

        Assert.Equal(2, store.LoadCount);
        Assert.Same(draft, viewModel.SelectedProject);
        Assert.Same(draft, Assert.Single(viewModel.Projects));
        Assert.Equal("Draft created during initialization", draft.DisplayName);
    }

    [Fact]
    public async Task BackDuringBlockedInitialization_PreservesCreatedRowAndExplicitListRoute()
    {
        var store = new BlockingBuilderProjectStore();
        await using var viewModel = CreateViewModel(store: store);
        var initialization = viewModel.InitializeAsync();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.CreateProjectAsync();
        var draft = Assert.IsType<BuilderProjectViewModel>(viewModel.SelectedProject);
        draft.DisplayName = "Draft kept in list";

        viewModel.BackToProjectList();
        viewModel.IsCompactLayout = true;
        viewModel.IsCompactLayout = false;
        store.ReleaseLoad.TrySetResult();
        await initialization;

        Assert.Same(draft, Assert.Single(viewModel.Projects));
        Assert.Null(viewModel.SelectedProject);
        Assert.Equal("Draft kept in list", draft.DisplayName);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Theory]
    [InlineData(nameof(BuilderProjectViewModel.DisplayName))]
    [InlineData(nameof(BuilderProjectViewModel.PackageId))]
    [InlineData(nameof(BuilderProjectViewModel.WorkspaceId))]
    [InlineData(nameof(BuilderProjectViewModel.WorkspacePathId))]
    [InlineData(nameof(BuilderProjectViewModel.ExecutionProjectFolder))]
    [InlineData(nameof(BuilderProjectViewModel.ProjectFolder))]
    [InlineData("SelectedFolder")]
    public async Task EditingAutoSelectedProject_PreservesDetailAcrossCompactResize(string field)
    {
        var persistedProject = CreateProject("one", "One Package");
        await using var viewModel = CreateViewModel(
            store: new SnapshotBuilderProjectStore(persistedProject.ToRecord()));
        await viewModel.InitializeAsync();
        var selectedProject = Assert.IsType<BuilderProjectViewModel>(viewModel.SelectedProject);

        switch (field)
        {
            case nameof(BuilderProjectViewModel.DisplayName):
                selectedProject.DisplayName = "Edited Package";
                break;
            case nameof(BuilderProjectViewModel.PackageId):
                selectedProject.PackageId = "local.edited";
                break;
            case nameof(BuilderProjectViewModel.WorkspaceId):
                selectedProject.WorkspaceId = "workspace.edited";
                break;
            case nameof(BuilderProjectViewModel.WorkspacePathId):
                selectedProject.WorkspacePathId = "workspace.edited.path";
                break;
            case nameof(BuilderProjectViewModel.ExecutionProjectFolder):
                selectedProject.ExecutionProjectFolder = "/tmp/execution-edited";
                break;
            case nameof(BuilderProjectViewModel.ProjectFolder):
                selectedProject.ProjectFolder = "/tmp/project-edited";
                break;
            case "SelectedFolder":
                viewModel.ApplySelectedFolder("/tmp/selected-folder-edited");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        viewModel.IsCompactLayout = true;

        Assert.Same(selectedProject, viewModel.SelectedProject);
        Assert.True(viewModel.ShowCompactEditor);
        Assert.False(viewModel.ShowCompactList);
    }

    [Fact]
    public async Task DeleteSelectedProjectAsync_WhenCompactLayout_ClearsSelectionAndReturnsToList()
    {
        var viewModel = CreateViewModel();
        var deletedProject = CreateProject("one", "One Package");
        var remainingProject = CreateProject("two", "Two Package");
        viewModel.Projects.Add(deletedProject);
        viewModel.Projects.Add(remainingProject);
        viewModel.IsCompactLayout = true;
        viewModel.ActivateProject(deletedProject);

        await viewModel.DeleteSelectedProjectAsync();

        Assert.DoesNotContain(deletedProject, viewModel.Projects);
        Assert.Contains(remainingProject, viewModel.Projects);
        Assert.Null(viewModel.SelectedProject);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.Equal(string.Empty, viewModel.StatusText);
        Assert.False(viewModel.ShowStatusMessage);
    }

    [Fact]
    public async Task DeleteSelectedProjectAsync_WhenWideLayout_SelectsNextProjectAndShowsSuccess()
    {
        var viewModel = CreateViewModel();
        var deletedProject = CreateProject("one", "One Package");
        var remainingProject = CreateProject("two", "Two Package");
        viewModel.Projects.Add(deletedProject);
        viewModel.Projects.Add(remainingProject);
        viewModel.ActivateProject(deletedProject);

        await viewModel.DeleteSelectedProjectAsync();

        Assert.DoesNotContain(deletedProject, viewModel.Projects);
        Assert.Same(remainingProject, viewModel.SelectedProject);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowEditorPane);
        Assert.Equal("Deleted package project 'One Package'.", viewModel.StatusText);
        Assert.True(viewModel.ShowStatusMessage);
    }

    [Fact]
    public async Task DeleteSelectedProjectAsync_LateSaveCannotClearNewSelectionAfterResize()
    {
        var store = new BlockingSaveBuilderProjectStore();
        await using var viewModel = CreateViewModel(store: store);
        var deletedProject = CreateProject("one", "One Package");
        var remainingProject = CreateProject("two", "Two Package");
        viewModel.Projects.Add(deletedProject);
        viewModel.Projects.Add(remainingProject);
        viewModel.IsCompactLayout = true;
        viewModel.ActivateProject(deletedProject);

        var deletion = viewModel.DeleteSelectedProjectAsync();
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.IsCompactLayout = false;
        viewModel.ActivateProject(remainingProject);
        store.ReleaseSave.TrySetResult();
        await deletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(remainingProject, viewModel.SelectedProject);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Fact]
    public async Task InitializeSelectedProjectAsync_WhenValid_EnqueuesMainIndicatorProcessAndLocksProjectIdentity()
    {
        var queue = new TestBackgroundProcessQueue();
        var viewModel = CreateViewModel(queue);
        var project = CreateProject("one", "One Package");
        AddWorkspace(viewModel, project.WorkspaceId);
        viewModel.Projects.Add(project);
        viewModel.ActivateProject(project);

        await viewModel.InitializeSelectedProjectAsync();

        var request = Assert.Single(queue.Requests);
        Assert.Equal("Initialize One Package", request.Title);
        Assert.Equal("sunder-package-builder", request.GroupKey);
        Assert.Equal(BackgroundProcessIndicator.Main, request.Indicator);
        Assert.True(viewModel.IsSelectedProjectInitializing);
        Assert.False(viewModel.CanEditSelectedProject);
        Assert.False(viewModel.CanEditProjectIdentity);
        Assert.False(viewModel.CanInitializeSelectedProject);
        Assert.Equal("Package initialization queued.", viewModel.StatusText);
    }

    [Fact]
    public async Task InitializeSelectedProjectAsync_WhenWorkspaceIsMissing_DoesNotQueueProcess()
    {
        var queue = new TestBackgroundProcessQueue();
        var viewModel = CreateViewModel(queue);
        var project = CreateProject("one", "One Package");
        project.WorkspaceId = string.Empty;
        viewModel.Projects.Add(project);
        viewModel.ActivateProject(project);

        await viewModel.InitializeSelectedProjectAsync();

        Assert.Empty(queue.Requests);
        Assert.False(viewModel.IsSelectedProjectInitializing);
        Assert.Equal("Workspace is required.", viewModel.StatusText);
    }

    [Fact]
    public async Task EnsureSelectedProjectSetupAsync_WhenValid_EnqueuesMainIndicatorProcess()
    {
        var queue = new TestBackgroundProcessQueue();
        var viewModel = CreateViewModel(queue);
        var project = CreateProject("one", "One Package");
        AddWorkspace(viewModel, project.WorkspaceId);
        viewModel.Projects.Add(project);
        viewModel.ActivateProject(project);

        await viewModel.EnsureSelectedProjectSetupAsync();

        var request = Assert.Single(queue.Requests);
        Assert.Equal("Check setup for One Package", request.Title);
        Assert.Equal("sunder-package-builder", request.GroupKey);
        Assert.Equal(BackgroundProcessIndicator.Main, request.Indicator);
        Assert.True(request.CanCancel);
        Assert.Equal("Setup check queued.", viewModel.StatusText);
        Assert.True(viewModel.CanRunSelectedProjectOperations);
    }

    [Fact]
    public async Task EnsureSelectedProjectSetupAsync_WhenWorkspaceIsMissing_DoesNotQueueProcess()
    {
        var queue = new TestBackgroundProcessQueue();
        var viewModel = CreateViewModel(queue);
        var project = CreateProject("one", "One Package");
        project.WorkspaceId = string.Empty;
        viewModel.Projects.Add(project);
        viewModel.ActivateProject(project);

        await viewModel.EnsureSelectedProjectSetupAsync();

        Assert.Empty(queue.Requests);
        Assert.Equal("Workspace is required.", viewModel.StatusText);
    }

    [Fact]
    public void ActivateProject_DefaultsWorkspacePathToWorkspaceDefault()
    {
        var viewModel = CreateViewModel();
        var project = CreateProject("one", "One Package");
        project.WorkspacePathId = string.Empty;
        var workspace = CreateWorkspace(
            project.WorkspaceId,
            new AgentWorkspacePathRecord("secondary", project.WorkspaceId, "/tmp/secondary", IsDefault: false, SortOrder: 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new AgentWorkspacePathRecord("primary", project.WorkspaceId, "/tmp/primary", IsDefault: true, SortOrder: 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );
        viewModel.Workspaces.Add(workspace);
        viewModel.Projects.Add(project);

        viewModel.ActivateProject(project);

        Assert.Equal("primary", project.WorkspacePathId);
        Assert.Equal(["secondary", "primary"], viewModel.WorkspacePathOptions.Select(path => path.PathId).ToArray());
    }

    [Fact]
    public void ChangingWorkspace_DefaultsWorkspacePathToNewWorkspaceDefault()
    {
        var viewModel = CreateViewModel();
        var project = CreateProject("one", "One Package");
        viewModel.Workspaces.Add(CreateWorkspace(project.WorkspaceId, "first-path", "/tmp/first", isDefault: true));
        viewModel.Workspaces.Add(CreateWorkspace("workspace.second", "second-path", "/tmp/second", isDefault: true));
        viewModel.Projects.Add(project);
        viewModel.ActivateProject(project);

        project.WorkspaceId = "workspace.second";

        Assert.Equal("second-path", project.WorkspacePathId);
        Assert.Equal(["second-path"], viewModel.WorkspacePathOptions.Select(path => path.PathId).ToArray());
    }

    [Fact]
    public async Task SelectedWorkspacePath_ResolvesProjectFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-builder-tests", Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var workspace = CreateWorkspace(
            "workspace.local",
            new AgentWorkspacePathRecord("first", "workspace.local", firstRoot, IsDefault: true, SortOrder: 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new AgentWorkspacePathRecord("second", "workspace.local", secondRoot, IsDefault: false, SortOrder: 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        );
        var resolver = new TestWorkspaceExecutionResolver(workspace);
        var executionService = new BuilderWorkspaceExecutionService(resolver);
        var viewModel = CreateViewModel(resolver: resolver);
        viewModel.Workspaces.Add(workspace);
        var project = CreateProject("one", "USB Lootbox");
        project.WorkspacePathId = "second";
        viewModel.Projects.Add(project);
        viewModel.ActivateProject(project);

        try
        {
            var execution = await executionService.ResolveAsync(workspace.WorkspaceId);
            var executionRoot = execution.ResolveExecutionWorkspacePath(workspace.Paths.Single(path => path.PathId == "second"));
            var executionProjectFolder = execution.CombinePath(executionRoot, "USBLootbox");
            var hostMapping = await execution.MapToHostPathAsync(executionProjectFolder);
            var expectedProjectFolder = Path.Combine(secondRoot, "USBLootbox");
            project.ProjectFolder = expectedProjectFolder;

            Assert.Equal(expectedProjectFolder, hostMapping.HostPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static BuilderViewModel CreateViewModel(
        TestBackgroundProcessQueue? queue = null,
        IAgentWorkspaceExecutionResolver? resolver = null,
        IBuilderProjectStore? store = null)
    {
        var packageContext = new TestPackageContext();
        store ??= new BuilderProjectStore(packageContext);
        var pathService = new BuilderPathService();
        var executionService = resolver is null
            ? new BuilderWorkspaceExecutionService()
            : new BuilderWorkspaceExecutionService(resolver);
        var backgroundProcesses = queue ?? new TestBackgroundProcessQueue();
        return new BuilderViewModel(
            new BuilderProjectApplicationService(
                new BuilderSetupService(),
                executionService,
                store,
                pathService),
            new BuilderOperationQueue(backgroundProcesses),
            new BuilderProjectPersistence(store),
            pathService,
            new ImmediateBuilderUiDispatcher());
    }

    private static void AddWorkspace(BuilderViewModel viewModel, string workspaceId)
    {
        viewModel.Workspaces.Add(CreateWorkspace(workspaceId, DefaultPathId(workspaceId), $"/tmp/{workspaceId}", isDefault: true));
    }

    private static AgentWorkspaceRecord CreateWorkspace(string workspaceId, string pathId, string hostPath, bool isDefault)
    {
        var now = DateTimeOffset.UtcNow;
        return CreateWorkspace(
            workspaceId,
            new AgentWorkspacePathRecord(pathId, workspaceId, hostPath, isDefault, SortOrder: 0, now, now)
        );
    }

    private static AgentWorkspaceRecord CreateWorkspace(string workspaceId, params AgentWorkspacePathRecord[] paths)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspaceRecord(workspaceId, "Local workspace", null, now, now, paths);
    }

    private static string DefaultPathId(string workspaceId) => workspaceId + ".path";

    private static BuilderProjectViewModel CreateProject(string id, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return new BuilderProjectViewModel(new BuilderProjectRecord(
            id,
            displayName,
            $"local.{id}",
            "workspace.local",
            $"/tmp/{id}",
            $"/tmp/{id}",
            now,
            now)
        {
            WorkspacePathId = DefaultPathId("workspace.local"),
        });
    }

    private sealed class TestExtensionCatalog : RegressionTestExtensionCatalog
    {
        public TestExtensionCatalog(IAgentWorkspaceExecutionResolver? resolver)
        {
            if (resolver is not null)
            {
                AddProvider(AgentRpcServices.WorkspaceExecutionResolvers, resolver);
            }
        }
    }

    private sealed class TestWorkspaceExecutionResolver : IAgentWorkspaceExecutionResolver
    {
        private readonly AgentWorkspaceRecord _workspace;
        private readonly TestExecutionTarget _target = new();
        private readonly RegressionTestExtensionCatalog _targetCatalog = new();

        public TestWorkspaceExecutionResolver(AgentWorkspaceRecord workspace)
        {
            _workspace = workspace;
            _targetCatalog.AddProvider(AgentRpcServices.ExecutionTargets, _target);
        }

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [_workspace];

        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var binding = new AgentWorkspaceBindingRecord(
                "binding.local",
                _workspace.WorkspaceId,
                AgentRpcContractIds.ExecutionTarget,
                "local",
                "primary",
                IsEnabled: true,
                SortOrder: 0,
                now,
                now
            );
            var scope = new AgentExecutionScopeDescriptor(
                "Local",
                _workspace.Paths.OrderBy(path => path.SortOrder).Select(path => path.HostPath).ToArray(),
                _workspace.Paths.OrderBy(path => path.SortOrder).FirstOrDefault(path => path.IsDefault)?.HostPath
            );
            var targetReference = _targetCatalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
            return ValueTask.FromResult(new AgentWorkspaceExecutionResolution(
                _workspace,
                binding,
                _target.Descriptor,
                scope,
                _target
            )
            {
                ExecutionTargetReference = targetReference,
                ExecutionTargetHandle = targetReference.ToHandle(),
            });
        }
    }

    private sealed class TestExecutionTarget : IAgentProcessExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "local",
            "local",
            "Local",
            null,
            SupportsShell: true,
            SupportsFiles: true
        );

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready"));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "Shell", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Shell"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, Exists: true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, string.Empty));

        public ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "dotnet" && request.Arguments.SequenceEqual(["--list-sdks"]))
            {
                return ValueTask.FromResult(new AgentShellCommandResult(0, "10.0.100 [/sdk]"));
            }

            if (request.FileName == "dotnet" && request.Arguments.SequenceEqual(["new", "list", "sunder-package"]))
            {
                return ValueTask.FromResult(new AgentShellCommandResult(0, "sunder-package"));
            }

            var outputIndex = IndexOf(request.Arguments, "--output");
            var nameIndex = IndexOf(request.Arguments, "--name");
            if (outputIndex >= 0 && outputIndex + 1 < request.Arguments.Count)
            {
                var outputPath = request.Arguments[outputIndex + 1];
                var projectName = nameIndex >= 0 && nameIndex + 1 < request.Arguments.Count
                    ? request.Arguments[nameIndex + 1]
                    : "GeneratedPackage";
                Directory.CreateDirectory(outputPath);
                File.WriteAllText(Path.Combine(outputPath, projectName + ".csproj"), "<Project />");
            }

            return ValueTask.FromResult(new AgentShellCommandResult(0, string.Empty));
        }

        private static int IndexOf(IReadOnlyList<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileReadResult(request.Path, string.Empty));

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Written"));

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Deleted"));
    }

    private sealed class TestPackageContext : IPackageContext
    {
        public string PackageId => "local.test.builder";

        public string Version { get; } = "1.0.0";

        public string ContentRootPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext();

        public IPackageSettings Settings { get; } = new TestSettings();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new TestFileStore();

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestPackageRoleLocalWorkspace(AppContext.BaseDirectory);
    }

    private sealed class TestFileStore : TestPackageFileStoreBase
    {
        public TestFileStore() : base(AppContext.BaseDirectory) { }
    }

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
    }

    private sealed class TestSettings : EmptyPackageSettings;

    private sealed class TestSecrets : InMemoryPackageSecrets;

    private sealed class TestBackgroundProcessQueue : IBackgroundProcessQueue
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

            return new(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundProcessState.Queued,
                string.Empty,
                ProgressPercent: null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(),
                ErrorMessage: null,
                DateTimeOffset.UtcNow,
                StartedAtUtc: null,
                CompletedAtUtc: null);
        }

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];

        public bool Cancel(Guid processId) => false;
    }

    private sealed class BlockingBuilderProjectStore : IBuilderProjectStore
    {
        public int LoadCount { get; private set; }
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LoadStarted.TrySetResult();
            if (LoadCount >= 2)
            {
                SecondLoadStarted.TrySetResult();
            }
            await ReleaseLoad.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class SnapshotBuilderProjectStore(params BuilderProjectRecord[] projects) : IBuilderProjectStore
    {
        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BuilderProjectRecord>>(projects);

        public Task SaveAsync(IReadOnlyList<BuilderProjectRecord> updatedProjects, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class BlockingSaveBuilderProjectStore : IBuilderProjectStore
    {
        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BuilderProjectRecord>>([]);

        public async Task SaveAsync(
            IReadOnlyList<BuilderProjectRecord> projects,
            CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailingBuilderProjectStore : IBuilderProjectStore
    {
        public Task<IReadOnlyList<BuilderProjectRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<BuilderProjectRecord>>(
                new InvalidOperationException("Injected builder load failure."));

        public Task SaveAsync(IReadOnlyList<BuilderProjectRecord> projects, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ImmediateBuilderUiDispatcher : IBuilderUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
}

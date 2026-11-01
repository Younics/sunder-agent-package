extern alias AgentApp;
extern alias DockerWorker;

using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using AgentApp::Sunder.Package.Agent.PackageViews;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Services;
using Xunit;
using AgentAppServiceIdentity = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentServiceIdentity;
using AppModels = AgentApp::Sunder.Package.Agent.Contracts.Models;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using static Sunder.Package.Agent.Tests.WorkspaceDockerFixture;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceEditingTests
{
    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_WarmsSelectedExecutionTarget()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("test-target");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var warmupService = new AgentExecutionTargetWarmupService(workspaceService, executionTargetService);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(workspaceService), AppExecution(warmupService), catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, target.Descriptor.TargetId, StringComparison.OrdinalIgnoreCase));
        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.Equal(1, target.ReadinessCallCount);
        Assert.Contains("Execution target is ready", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_RefreshesExecutionTargets_WhenCatalogChanges()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        await using var runtime = RegressionTestRuntime.Create(scope.Context, catalog);
        var owner = (AgentOwnerRuntimeProvider)runtime.GetService(typeof(AgentOwnerRuntimeProvider))!;
        catalog.AddGeneratedProvider("younics.ai.agent", owner, owner.CreateSunderRpcProviderRegistration());
        var rpc = catalog.RpcForCaller("younics.ai.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var owners = receiver.Providers<AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IAgentRuntimeService>();
        using var gateway = new AppRuntime.AgentAppRuntimeGateway(Assert.Single((await owners.ListAsync()).Providers).Service,
            new AppRuntime.AgentOwnerResources(owners, rpc));
        using var viewModel = new AgentWorkspacesViewModel(gateway, gateway, catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        Assert.False(viewModel.HasExecutionTargetChoices);
        Assert.True(viewModel.HasNoExecutionTargetChoices);

        var target = new CountingExecutionTarget("docker");
        await WaitForExecutionTargetAddedAsync(
            viewModel,
            "docker",
            () => catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target));

        Assert.True(viewModel.HasExecutionTargetChoices);
        Assert.False(viewModel.HasNoExecutionTargetChoices);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_RefreshesEditorSections_WhenCatalogChanges()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        await using var runtime = RegressionTestRuntime.Create(scope.Context, catalog);
        var owner = (AgentOwnerRuntimeProvider)runtime.GetService(typeof(AgentOwnerRuntimeProvider))!;
        catalog.AddGeneratedProvider("younics.ai.agent", owner, owner.CreateSunderRpcProviderRegistration());
        var rpc = catalog.RpcForCaller("younics.ai.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var owners = receiver.Providers<AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IAgentRuntimeService>();
        using var gateway = new AppRuntime.AgentAppRuntimeGateway(Assert.Single((await owners.ListAsync()).Providers).Service,
            new AppRuntime.AgentOwnerResources(owners, rpc));
        var editorCatalog = new ObservedServiceProviders<AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IWorkspaceEditorService>(catalog.WorkspaceEditors);
        using var viewModel = new AgentWorkspacesViewModel(gateway, gateway, editorCatalog);
        await viewModel.InitializeAsync();
        await editorCatalog.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.CreateWorkspaceCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.SelectedWorkspace);
        var target = new CountingExecutionTarget("docker");
        await WaitForExecutionTargetAddedAsync(
            viewModel,
            "docker",
            () => catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target));
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(viewModel.EditorSections);

        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, new TestWorkspaceEditorContributor("docker"));

        await WaitUntilAsync(() => viewModel.EditorSections.Any(section => string.Equals(section.SectionId, "test-editor", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_LoadWorkspace_RefreshesEditorSectionsOnce()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var workspace = workspaceService.CreateWorkspace("Docker Workspace");
        workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, target.Descriptor.TargetId);

        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(workspaceService), AppExecution(executionTargetService), catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);
        Assert.Equal(1, contributor.GetSectionsCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentWorkspacesViewModel_BindingOnlyChangesRefreshCleanSelectionButPreserveDrafts(bool dirty)
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new CountingExecutionTarget("docker"));
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new CountingExecutionTarget("local"));
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, new CountingWorkspaceEditorContributor("docker"));
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, new CountingWorkspaceEditorContributor("local"));
        var service = new AgentWorkspaceService(store);
        var workspace = service.CreateWorkspace("Workspace");
        service.SavePrimaryExecutionBinding(workspace.WorkspaceId, "docker");
        using var model = new AgentWorkspacesViewModel(AppWorkspace(service),
            AppExecution(new AgentExecutionTargetService(catalog.ExecutionTargets)), catalog.WorkspaceEditors);
        await model.InitializeAsync();
        await model.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        if (dirty) model.DisplayName = "Unsaved name";

        service.SavePrimaryExecutionBinding(workspace.WorkspaceId, "local");
        await model.CurrentRuntimeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        await model.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(workspace.UpdatedAtUtc, service.GetWorkspace(workspace.WorkspaceId)!.UpdatedAtUtc);
        Assert.Equal(dirty ? "docker" : "local", model.SelectedExecutionTarget?.TargetId);
        Assert.Equal(dirty ? "docker" : "local", Assert.Single(model.EditorSections).Context.TargetId);
        Assert.Equal(dirty ? "Unsaved name" : "Workspace", model.DisplayName);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_DoesNotRefreshEditorSectionsAndReturnsToList()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(workspaceService), AppExecution(executionTargetService), catalog.WorkspaceEditors)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);
        var getSectionsCallCount = contributor.GetSectionsCallCount;
        viewModel.DisplayName = "Saved Docker Workspace";
        var savedWorkspaceId = viewModel.SelectedWorkspace!.WorkspaceId;

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Empty(viewModel.EditorSections);
        Assert.Equal(getSectionsCallCount, contributor.GetSectionsCallCount);
        Assert.Equal(1, contributor.SaveSectionCallCount);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.False(viewModel.HasSelectedWorkspace);
        Assert.False(viewModel.SaveWorkspaceCommand.CanExecute(null));
        Assert.Contains(viewModel.Workspaces, workspace => workspace.WorkspaceId == savedWorkspaceId
                                                        && workspace.DisplayName == "Saved Docker Workspace");
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(workspaceService), AppExecution(executionTargetService), catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);
        var getSectionsCallCount = contributor.GetSectionsCallCount;
        var savedWorkspaceId = viewModel.SelectedWorkspace!.WorkspaceId;
        viewModel.DisplayName = "Saved Docker Workspace";

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Equal(savedWorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Saved Docker Workspace", viewModel.DisplayName);
        Assert.Single(viewModel.EditorSections);
        Assert.Equal(getSectionsCallCount, contributor.GetSectionsCallCount);
        Assert.Equal(1, contributor.SaveSectionCallCount);
        Assert.Equal("docker", viewModel.SelectedExecutionTarget?.TargetId);
        Assert.Equal("Workspace saved.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
        Assert.True(viewModel.IsStatusSuccess);
        Assert.False(viewModel.IsStatusWarning);
        Assert.False(viewModel.IsStatusError);

        await WaitUntilAsync(() => !viewModel.HasStatusText, TimeSpan.FromSeconds(4));

        Assert.Empty(viewModel.StatusText);
        Assert.Equal(AgentWorkspaceStatusKind.None, viewModel.StatusKind);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_FailedEditorSaveKeepsEditorOpen()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new CountingWorkspaceEditorContributor("docker", AgentEditorSaveResult.Failed("Editor save failed."));
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(workspaceService), AppExecution(executionTargetService), catalog.WorkspaceEditors)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Count == 1);

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal("Editor save failed.", viewModel.StatusText);
        Assert.Single(viewModel.EditorSections);
        Assert.Equal(1, contributor.SaveSectionCallCount);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SaveWorkspace_SelectionChangesDuringEditorSavePersistOriginalSnapshot()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new BlockingWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var originalWorkspace = workspaceService.CreateWorkspace("Original Workspace");
        var otherWorkspace = workspaceService.CreateWorkspace("Other Workspace");
        using var viewModel = new AgentWorkspacesViewModel(
            AppWorkspace(workspaceService),
            AppExecution(executionTargetService),
            catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        viewModel.ActivateWorkspace(ToAppWorkspace(originalWorkspace));
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(option =>
            string.Equals(option.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await contributor.SectionsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.DisplayName = "Saved Original Workspace";

        var save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
        await contributor.SaveEntered.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.ActivateWorkspace(ToAppWorkspace(otherWorkspace));
        contributor.ReleaseSave();
        await save.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(otherWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Other Workspace", viewModel.DisplayName);
        Assert.Empty(viewModel.StatusText);
        Assert.Equal(
            "Saved Original Workspace",
            workspaceService.GetWorkspace(originalWorkspace.WorkspaceId)?.DisplayName);
        Assert.Equal(
            "Other Workspace",
            workspaceService.GetWorkspace(otherWorkspace.WorkspaceId)?.DisplayName);
        Assert.Equal(originalWorkspace.WorkspaceId, contributor.SavedWorkspaceId);
        Assert.Contains(
            workspaceService.ListBindings(originalWorkspace.WorkspaceId),
            binding => binding.ContributionId == "docker");
        Assert.Empty(workspaceService.ListBindings(otherWorkspace.WorkspaceId));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_DirtyKeyedDraftSurvivesRuntimeRefreshAndLateSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-workspace-draft-tests", Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "first");
        var secondPath = Path.Combine(root, "second");
        var firstDocument = Path.Combine(root, "first.md");
        var secondDocument = Path.Combine(root, "second.md");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        await File.WriteAllTextAsync(firstDocument, "first");
        await File.WriteAllTextAsync(secondDocument, "second");
        try
        {
            using var scope = TestScope.Create();
            var store = new AgentLocalStore(scope.Context);
            using var catalog = new TestExtensionCatalog();
            var target = new CountingExecutionTarget("docker");
            var contributor = new BlockingWorkspaceEditorContributor("docker");
            catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
            using var changeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var changes = catalog.WorkspaceEditors.WatchAsync(0, 0, changeCancellation.Token).GetAsyncEnumerator();
            try
            {
                catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
                Assert.True(await changes.MoveNextAsync());
            }
            finally { changeCancellation.Cancel(); }
            var workspaceService = new AgentWorkspaceService(store);
            var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
            var workspace = workspaceService.CreateWorkspace("Original Workspace");
            workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, "docker");
            using var viewModel = new AgentWorkspacesViewModel(
                AppWorkspace(workspaceService),
                AppExecution(executionTargetService),
                catalog.WorkspaceEditors);
            await viewModel.InitializeAsync();
            await viewModel.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(2));
            var section = Assert.Single(viewModel.EditorSections);
            var contributedField = Assert.IsType<AgentEditorTextFieldViewModel>(Assert.Single(section.Fields));
            viewModel.DisplayName = "Snapshot name";
            viewModel.Description = "Snapshot description";
            viewModel.AddWorkspacePath(firstPath);
            viewModel.AddWorkspaceDocument(firstDocument);
            contributedField.Value = "snapshot contributed value";

            var save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
            await contributor.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.DisplayName = "Newer draft name";
            viewModel.Description = "Newer draft description";
            viewModel.AddWorkspacePath(secondPath);
            viewModel.AddWorkspaceDocument(secondDocument);
            contributedField.Value = "newer contributed value";
            var pathRow = viewModel.WorkspacePaths[0];
            var documentRow = viewModel.WorkspaceDocuments[0];
            pathRow.BeginEdit();
            pathRow.EditHostPath = "unfinished inline edit";
            workspaceService.SaveWorkspace(workspace.WorkspaceId, "Runtime refresh value", "Runtime refresh description");
            await viewModel.CurrentRuntimeRefresh.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("Newer draft name", viewModel.DisplayName);
            Assert.Equal("Newer draft description", viewModel.Description);
            Assert.Equal(2, viewModel.WorkspacePaths.Count);
            Assert.Equal(2, viewModel.WorkspaceDocuments.Count);
            Assert.Same(pathRow, viewModel.WorkspacePaths[0]);
            Assert.Same(documentRow, viewModel.WorkspaceDocuments[0]);
            Assert.True(pathRow.IsEditActive);
            Assert.Equal("unfinished inline edit", pathRow.EditHostPath);
            Assert.Same(section, Assert.Single(viewModel.EditorSections));
            Assert.Equal("newer contributed value", contributedField.Value);

            viewModel.SelectedExecutionTarget = ExecutionTargetOption.Unconfigured;
            contributor.ReleaseSave();
            await save.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
            Assert.Equal("Newer draft name", viewModel.DisplayName);
            Assert.Equal("Newer draft description", viewModel.Description);
            Assert.Equal(2, viewModel.WorkspacePaths.Count);
            Assert.Equal(2, viewModel.WorkspaceDocuments.Count);
            Assert.True(viewModel.SelectedExecutionTarget?.IsUnconfigured);
            Assert.Contains("remain unsaved", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.True(section.CanRetain(section.ContributorReference, section.Context,
                section.Descriptor! with { Title = "External metadata after save" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_ResizeDuringSaveCannotCloseCurrentDraft()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new BlockingWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var workspace = workspaceService.CreateWorkspace("Workspace");
        workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, "docker");
        using var viewModel = new AgentWorkspacesViewModel(
            AppWorkspace(workspaceService),
            AppExecution(new AgentExecutionTargetService(catalog.ExecutionTargets)),
            catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        await contributor.SectionsLoaded.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.DisplayName = "Saved workspace";

        var save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
        await contributor.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.IsCompactLayout = true;
        contributor.ReleaseSave();
        await save.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_StaleEditorSectionsCannotReplaceNewSelectionSections()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("docker");
        var contributor = new SelectionRaceWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var first = workspaceService.CreateWorkspace("First Workspace");
        var second = workspaceService.CreateWorkspace("Second Workspace");
        workspaceService.SavePrimaryExecutionBinding(first.WorkspaceId, "docker");
        workspaceService.SavePrimaryExecutionBinding(second.WorkspaceId, "docker");
        using var viewModel = new AgentWorkspacesViewModel(
            AppWorkspace(workspaceService),
            AppExecution(executionTargetService),
            catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        await contributor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var staleRefresh = viewModel.CurrentEditorSectionRefresh;

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == second.WorkspaceId));
        var currentRefresh = viewModel.CurrentEditorSectionRefresh;
        await currentRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Second Workspace", Assert.Single(viewModel.EditorSections).Title);

        contributor.ReleaseFirst.TrySetResult();
        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(second.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Second Workspace", viewModel.DisplayName);
        Assert.Equal("Second Workspace", Assert.Single(viewModel.EditorSections).Title);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_CreateWorkspace_CompactLayout_OpensEditor()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);

        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
        Assert.False(viewModel.ShowCompactList);
        Assert.Equal("New Workspace", viewModel.SelectedWorkspace?.DisplayName);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_CompactLayout_ClearsDefaultWorkspaceSelection()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);

        viewModel.IsCompactLayout = true;

        Assert.Null(viewModel.SelectedWorkspace);
        Assert.False(viewModel.HasSelectedWorkspace);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.False(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_FirstEditPromotesAutomaticSelectionBeforeCompactResize()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(
            AppWorkspace(services.WorkspaceService),
            AppExecution(services.ExecutionTargetService),
            services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.DisplayName = "Edited automatic workspace";
        viewModel.IsCompactLayout = true;

        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal("Edited automatic workspace", viewModel.DisplayName);
        Assert.True(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactEditor);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_SupersededInitializationRetriesLatestWorkspaceSnapshot()
    {
        var gateway = new BlockingWorkspaceInitializationGateway();
        using var catalog = new TestExtensionCatalog();
        using var viewModel = new AgentWorkspacesViewModel(
            AppWorkspace(gateway),
            AppExecution(new AgentExecutionTargetService(catalog.ExecutionTargets)),
            catalog.WorkspaceEditors);

        var initialization = viewModel.InitializeAsync();
        try
        {
            await gateway.FirstInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.CreateWorkspaceCommand.Execute(null);
            var selectionBeforeRetry = Assert.IsType<AppModels.AgentWorkspaceRecord>(viewModel.SelectedWorkspace);
            await gateway.SecondInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.DisplayName = "Unsaved workspace draft";
            gateway.AdvanceWorkspaceSnapshot();

            gateway.ReleaseSecondInitialization.TrySetResult();
            await initialization.WaitAsync(TimeSpan.FromSeconds(2));
            gateway.ReleaseFirstInitialization.TrySetResult();
            await gateway.FirstInitializationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Yield();

            Assert.Equal(2, gateway.InitializeCount);
            Assert.Equal("ExistingDetail", viewModel.Route.ToString());
            Assert.Equal("Ready", viewModel.DetailPhase.ToString());
            var currentWorkspace = Assert.Single(viewModel.Workspaces);
            Assert.Equal("New Workspace", currentWorkspace.DisplayName);
            Assert.Same(currentWorkspace, viewModel.SelectedWorkspace);
            Assert.NotSame(selectionBeforeRetry, viewModel.SelectedWorkspace);
            Assert.Equal("Unsaved workspace draft", viewModel.DisplayName);
        }
        finally
        {
            gateway.ReleaseSecondInitialization.TrySetResult();
            gateway.ReleaseFirstInitialization.TrySetResult();
        }
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_WideLayout_SelectsFirstWorkspace_WhenCompactHadNoSelection()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(
            AppWorkspace(services.WorkspaceService),
            AppExecution(services.ExecutionTargetService),
            services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        viewModel.IsCompactLayout = true;
        Assert.Null(viewModel.SelectedWorkspace);

        viewModel.IsCompactLayout = false;

        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_ActivateWorkspace_CompactLayout_OpensSelectedWorkspaceEditor()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var workspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();
        Assert.False(viewModel.IsEditorActive);

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(item => item.WorkspaceId == workspace.WorkspaceId));

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.True(viewModel.ShowCompactEditor);

        viewModel.BackToWorkspaceListCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.True(viewModel.ShowCompactList);
        Assert.False(viewModel.ShowCompactEditor);

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(item => item.WorkspaceId == workspace.WorkspaceId));

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(workspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_ActivateWorkspace_WideLayout_KeepsSplitPanesVisible()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var firstWorkspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        var secondWorkspace = services.WorkspaceService.CreateWorkspace("Beta Workspace");
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        Assert.Equal(firstWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);

        viewModel.ActivateWorkspace(viewModel.Workspaces.Single(item => item.WorkspaceId == secondWorkspace.WorkspaceId));

        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
        Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_DeleteWorkspace_CompactLayout_ReturnsToList()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var firstWorkspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        var secondWorkspace = services.WorkspaceService.CreateWorkspace("Beta Workspace");
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(item => item.WorkspaceId == secondWorkspace.WorkspaceId);
        Assert.True(viewModel.IsEditorActive);

        viewModel.DeleteWorkspaceCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);
        Assert.False(viewModel.ShowCompactEditor);
        Assert.Single(viewModel.Workspaces);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.Contains(viewModel.Workspaces, workspace => workspace.WorkspaceId == firstWorkspace.WorkspaceId);
        Assert.Null(services.WorkspaceService.GetWorkspace(secondWorkspace.WorkspaceId));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_DeleteWorkspace_WideLayout_SelectsFirstRemainingWorkspace()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var firstWorkspace = services.WorkspaceService.CreateWorkspace("Alpha Workspace");
        var secondWorkspace = services.WorkspaceService.CreateWorkspace("Beta Workspace");
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(item => item.WorkspaceId == secondWorkspace.WorkspaceId);

        viewModel.DeleteWorkspaceCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
        Assert.Single(viewModel.Workspaces);
        Assert.Equal(firstWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Null(services.WorkspaceService.GetWorkspace(secondWorkspace.WorkspaceId));
    }

    [Fact]
    public void AgentEditorPathListFieldViewModel_PreservesDefaultWithoutInitialSelection()
    {
        using var catalog = new TestExtensionCatalog();
        var section = CreateEditorSectionViewModel(catalog, new AgentEditorField(
            "workspace-paths",
            "Workspace paths",
            AgentEditorFieldKind.PathList,
            Items:
            [
                new AgentEditorListItem("0", "/workspace", IsDefault: true),
                new AgentEditorListItem("1", "/tmp"),
            ]));

        var field = Assert.IsType<AgentEditorPathListFieldViewModel>(Assert.Single(section.Fields));

        Assert.Null(field.SelectedItem);
        Assert.False(field.HasSelectedItem);
        Assert.True(field.Items[0].IsDefault);
        Assert.False(field.Items[1].IsDefault);
    }

    [Fact]
    public void AgentEditorPathListFieldViewModel_AddDefaultItem_SelectsNewUserItem()
    {
        using var catalog = new TestExtensionCatalog();
        var section = CreateEditorSectionViewModel(catalog, new AgentEditorField(
            "workspace-paths",
            "Workspace paths",
            AgentEditorFieldKind.PathList,
            Items:
            [
                new AgentEditorListItem("0", "/workspace", IsDefault: true),
            ],
            DefaultNewItemValue: "/workspace"));
        var field = Assert.IsType<AgentEditorPathListFieldViewModel>(Assert.Single(section.Fields));

        field.AddDefaultItem();

        Assert.NotNull(field.SelectedItem);
        Assert.True(field.HasSelectedItem);
        Assert.Equal("/workspace2", field.SelectedItem!.Value);
        Assert.True(field.Items[0].IsDefault);
        Assert.False(field.SelectedItem.IsDefault);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_RefreshField_UpdatesDockerImageSelectOnly()
    {
        using var scope = TestScope.Create();
        var readyImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            var image = args.Count >= 3
                        && string.Equals(args[0], "image", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(args[1], "inspect", StringComparison.OrdinalIgnoreCase)
                ? args[2]
                : string.Empty;
            return Task.FromResult(new DockerCliRunResult(
                readyImages.Contains(image) ? 0 : 1,
                readyImages.Contains(image) ? "[]" : "No such image",
                TimedOut: false,
                WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.SaveImagesAsync(
        [
            new DockerImageDefinition("first:1.0", DockerImageStatus.NotPulled, null, null),
            new DockerImageDefinition("second:1.0", DockerImageStatus.NotPulled, null, null),
        ]);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new CountingExecutionTarget("docker"));
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(workspaceService), AppExecution(executionTargetService), catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        viewModel.SelectedExecutionTarget = viewModel.ExecutionTargets.Single(targetOption => string.Equals(targetOption.TargetId, "docker", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => viewModel.EditorSections.Any(section => string.Equals(section.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase)));
        await viewModel.CurrentEditorSectionRefresh.WaitAsync(TimeSpan.FromSeconds(2));

        var section = viewModel.EditorSections.Single(section => string.Equals(section.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase));
        var imageField = Assert.IsType<AgentEditorSelectFieldViewModel>(section.Fields.Single(field => field.FieldId == "image"));
        var shellField = Assert.IsType<AgentEditorTextFieldViewModel>(section.Fields.Single(field => field.FieldId == "shell-path"));
        shellField.Value = "/custom/sh";
        var refreshAction = Assert.Single(imageField.IconActions);
        Assert.Equal(AgentEditorActionKind.RefreshField, refreshAction.Kind);
        Assert.Equal("↻", refreshAction.Content);
        Assert.Empty(imageField.Options);
        Assert.False(imageField.HasOptions);
        Assert.True(imageField.HasNoOptions);
        Assert.True(imageField.ShowEmptyStateActions);
        Assert.Contains(imageField.TextActions, action => action.Kind == AgentEditorActionKind.OpenPackageSettings);

        readyImages.Add("second:1.0");
        await viewModel.ExecuteEditorActionAsync(refreshAction);

        Assert.Same(section, viewModel.EditorSections.Single(item => string.Equals(item.SectionId, "docker-execution-settings", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("/custom/sh", shellField.Value);
        var option = Assert.Single(imageField.Options);
        Assert.Equal("second:1.0", option.Value);
        Assert.Equal("second:1.0", imageField.SelectedOption?.Value);
        Assert.True(imageField.HasOptions);
        Assert.False(imageField.HasNoOptions);
        Assert.False(imageField.ShowEmptyStateActions);
        Assert.Single(imageField.IconActions);
        Assert.Empty(imageField.TextActions);
        Assert.Contains("Choose a ready", imageField.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_CanSaveWorkspaceWithoutAgentProfile()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedWorkspace);
        Assert.True(viewModel.SaveWorkspaceCommand.CanExecute(null));
    }

    [Fact]
    public async Task AgentWorkspacesViewModel_WorkspacePath_UsesTildeDisplayAndPersistsFullPath()
    {
        using var scope = TestScope.Create();
        using var services = CreateWorkspaceViewServices(scope.Context);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var viewModel = new AgentWorkspacesViewModel(AppWorkspace(services.WorkspaceService), AppExecution(services.ExecutionTargetService), services.Catalog.WorkspaceEditors);
        await viewModel.InitializeAsync();

        viewModel.CreateWorkspaceCommand.Execute(null);
        var workspaceId = viewModel.SelectedWorkspace!.WorkspaceId;
        viewModel.AddWorkspacePath(home);

        Assert.Equal("~", Assert.Single(viewModel.WorkspacePaths).HostPath);

        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        var savedPath = Assert.Single(services.WorkspaceService.GetWorkspace(workspaceId)!.Paths);
        Assert.Equal(Path.GetFullPath(home), savedPath.HostPath);
    }

    private static AgentEditorSectionViewModel CreateEditorSectionViewModel(TestExtensionCatalog catalog, params AgentEditorField[] fields)
    {
        var workspace = CreateWorkspace();
        var contributor = new TestWorkspaceEditorContributor("docker");
        catalog.AddProvider(RegressionTestAppBindings.WorkspaceEditors, contributor);
        return new AgentEditorSectionViewModel(
            catalog.GetRequiredProviderAsync(catalog.WorkspaceEditors).AsTask().GetAwaiter().GetResult(),
            new AgentWorkspaceEditorContext(ToAppWorkspace(workspace), "docker", CreateBinding(workspace.WorkspaceId, "docker").BindingId),
            new AgentEditorSection("test-section", "Test Section", null, fields));
    }

    private static AppRuntime.IAgentWorkspaceGateway AppWorkspace(object value)
        => AgentRoleBoundary.As<AppRuntime.IAgentWorkspaceGateway>(value);

    private static AppRuntime.IAgentExecutionGateway AppExecution(object value)
        => AgentRoleBoundary.As<AppRuntime.IAgentExecutionGateway>(value);

    private static async Task WaitForExecutionTargetAddedAsync(
        AgentWorkspacesViewModel viewModel,
        string targetId,
        Action trigger)
    {
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
        {
            if (eventArgs.NewItems?.OfType<ExecutionTargetOption>().Any(target => string.Equals(
                    target.TargetId,
                    targetId,
                    StringComparison.OrdinalIgnoreCase)) == true)
            {
                added.TrySetResult();
            }
        }

        viewModel.ExecutionTargets.CollectionChanged += OnCollectionChanged;
        try
        {
            trigger();
            await added.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            viewModel.ExecutionTargets.CollectionChanged -= OnCollectionChanged;
        }
    }

    private static AppModels.AgentWorkspaceRecord ToAppWorkspace(AgentWorkspaceRecord workspace)
        => new(
            workspace.WorkspaceId,
            workspace.DisplayName,
            workspace.Description,
            workspace.CreatedAtUtc,
            workspace.UpdatedAtUtc,
            workspace.Paths.Select(path => new AppModels.AgentWorkspacePathRecord(
                path.PathId,
                path.WorkspaceId,
                path.HostPath,
                path.IsDefault,
                path.SortOrder,
                path.CreatedAtUtc,
                path.UpdatedAtUtc)).ToArray(),
            workspace.Documents.Select(document => new AppModels.AgentWorkspaceDocumentRecord(
                document.DocumentId,
                document.WorkspaceId,
                document.FilePath,
                document.SortOrder,
                document.CreatedAtUtc,
                document.UpdatedAtUtc)).ToArray());

    private sealed class ObservedServiceProviders<TService>(IServiceProviders<TService> inner)
        : IServiceProviders<TService> where TService : class
    {
        public TaskCompletionSource WatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ServiceProviderCatalog<TService>> ListAsync(CancellationToken cancellationToken = default)
            => inner.ListAsync(cancellationToken);

        public async IAsyncEnumerable<ServiceProviderCatalogEvent<TService>> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var watch = inner.WatchAsync(afterRevision, afterSequence, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var moveNext = watch.MoveNextAsync();
            WatchStarted.TrySetResult();
            while (await moveNext.ConfigureAwait(false))
            {
                yield return watch.Current;
                moveNext = watch.MoveNextAsync();
            }
        }
    }

    private sealed class TestWorkspaceEditorContributor(string targetId) : IAgentWorkspaceEditorContributor
    {
        public string ContributorId => "test-workspace-editor";

        public ValueTask<AgentAppServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AgentAppServiceIdentity>(new AgentAppServiceIdentity(ContributorId, ContributorId));
        }

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CanEdit(context));
        }

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [
                new AgentEditorSection("test-editor", "Test Editor", null, []),
            ]);

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
    }

    private sealed class CountingWorkspaceEditorContributor(string targetId, AgentEditorSaveResult? saveResult = null) : IAgentWorkspaceEditorContributor
    {
        private int _getSectionsCallCount;
        private int _saveSectionCallCount;

        public string ContributorId => "counting-workspace-editor";

        public ValueTask<AgentAppServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AgentAppServiceIdentity>(new AgentAppServiceIdentity(ContributorId, ContributorId));
        }

        public int GetSectionsCallCount => Volatile.Read(ref _getSectionsCallCount);

        public int SaveSectionCallCount => Volatile.Read(ref _saveSectionCallCount);

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CanEdit(context));
        }

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getSectionsCallCount);
            return ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [
                new AgentEditorSection("counting-editor", "Counting Editor", null, []),
            ]);
        }

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveSectionCallCount);
            return ValueTask.FromResult(saveResult ?? AgentEditorSaveResult.Ok("Saved."));
        }
    }

    private sealed class BlockingWorkspaceEditorContributor(string targetId)
        : IAgentWorkspaceEditorContributor
    {
        private readonly TaskCompletionSource _sectionsLoaded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _saveEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ContributorId => "blocking-workspace-editor";

        public ValueTask<AgentAppServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AgentAppServiceIdentity>(new AgentAppServiceIdentity(ContributorId, ContributorId));
        }

        public Task SectionsLoaded => _sectionsLoaded.Task;

        public Task SaveEntered => _saveEntered.Task;

        public string? SavedWorkspaceId { get; private set; }

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CanEdit(context));
        }

        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            _sectionsLoaded.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<AgentEditorSection>>(
            [
                new AgentEditorSection(
                    "blocking-editor",
                    "Blocking Editor",
                    null,
                    [new AgentEditorField("value", "Value", AgentEditorFieldKind.Text, Value: "initial")]),
            ]);
        }

        public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            SavedWorkspaceId = context.Workspace.WorkspaceId;
            _saveEntered.TrySetResult();
            await _releaseSave.Task.WaitAsync(cancellationToken);
            return AgentEditorSaveResult.Ok("Saved.");
        }

        public void ReleaseSave() => _releaseSave.TrySetResult();
    }

    private sealed class BlockingWorkspaceInitializationGateway : IAgentWorkspaceGateway
    {
        private readonly object _syncRoot = new();
        private readonly List<AgentWorkspaceRecord> _workspaces = [];
        private int _initializeCount;
        private long _snapshotVersion;

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public TaskCompletionSource FirstInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstInitializationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondInitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecondInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? WorkspacesChanged;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
        {
            lock (_syncRoot)
            {
                return _workspaces.Select(workspace => workspace with
                {
                    UpdatedAtUtc = workspace.UpdatedAtUtc.AddTicks(_snapshotVersion),
                }).ToArray();
            }
        }

        public void AdvanceWorkspaceSnapshot()
        {
            lock (_syncRoot)
            {
                _snapshotVersion++;
            }
        }

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
        {
            lock (_syncRoot)
            {
                return _workspaces.FirstOrDefault(workspace => workspace.WorkspaceId == workspaceId);
            }
        }

        public AgentWorkspaceRecord CreateWorkspace(string displayName)
        {
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord(
                Guid.NewGuid().ToString("N"),
                displayName,
                null,
                now,
                now);
            lock (_syncRoot)
            {
                _workspaces.Add(workspace);
            }
            WorkspacesChanged?.Invoke();
            return workspace;
        }

        public void SaveWorkspace(string workspaceId, string displayName, string? description)
        {
            lock (_syncRoot)
            {
                var index = _workspaces.FindIndex(workspace => workspace.WorkspaceId == workspaceId);
                _workspaces[index] = _workspaces[index] with
                {
                    DisplayName = displayName,
                    Description = description,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
            }
            WorkspacesChanged?.Invoke();
        }

        public void SaveWorkspaceAggregate(
            string workspaceId,
            string displayName,
            string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths,
            IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId)
            => SaveWorkspace(workspaceId, displayName, description);

        public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _workspaces.RemoveAll(workspace => workspace.WorkspaceId == workspaceId);
            }
            WorkspacesChanged?.Invoke();
            return Task.CompletedTask;
        }

        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId) => [];

        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
            string workspaceId,
            string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
            => throw new NotSupportedException();

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            var initialization = Interlocked.Increment(ref _initializeCount);
            if (initialization == 1)
            {
                FirstInitializationStarted.TrySetResult();
                await ReleaseFirstInitialization.Task;
                FirstInitializationCompleted.TrySetResult();
            }
            else if (initialization == 2)
            {
                SecondInitializationStarted.TrySetResult();
                await ReleaseSecondInitialization.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class SelectionRaceWorkspaceEditorContributor(string targetId)
        : IAgentWorkspaceEditorContributor
    {
        private int _calls;

        public string ContributorId => "selection-race-workspace-editor";

        public ValueTask<AgentAppServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AgentAppServiceIdentity>(new AgentAppServiceIdentity(ContributorId, ContributorId));
        }

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanEdit(AgentWorkspaceEditorContext context)
            => string.Equals(context.TargetId, targetId, StringComparison.OrdinalIgnoreCase);

        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CanEdit(context));
        }

        public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
            AgentWorkspaceEditorContext context,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task;
            }

            return
            [
                new AgentEditorSection(
                    "selection-race-editor",
                    context.Workspace.DisplayName,
                    null,
                    []),
            ];
        }

        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
            AgentWorkspaceEditorContext context,
            AgentEditorSaveRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentEditorSaveResult.Ok("Saved."));
    }
}

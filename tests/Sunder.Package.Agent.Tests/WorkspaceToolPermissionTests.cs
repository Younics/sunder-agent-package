extern alias DockerWorker;
extern alias FilesWorker;
extern alias LocalWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using FilesWorker::Sunder.Package.Agent.Tools.Files;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceDockerFixture;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceToolPermissionTests
{
    [Fact]
    public async Task PermissionService_UsesLastMatchingRule_AndUnrestrictedModeCannotBypassDeny()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var permissions = new AgentPermissionService(store, catalog.PermissionSurfaces);
        var workspace = CreateWorkspace();
        store.SaveWorkspace(workspace);
        var session = store.CreateSession("Permission Test", workspaceId: workspace.WorkspaceId);

        catalog.AddProvider(RegressionTestBindings.PermissionSurfaces, new TestPermissionSurface());
        permissions.SaveOverride("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, AgentPermissionDecision.Deny);
        permissions.SetSessionUnrestrictedMode(session.SessionId, true);

        var denied = await permissions.EvaluateAsync(session.SessionId, new AgentPermissionRequest("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, "remove", Command: "rm -rf tmp"));
        permissions.SaveOverride("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, AgentPermissionDecision.Ask);
        var allowed = await permissions.EvaluateAsync(session.SessionId, new AgentPermissionRequest("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, "list", Command: "ls"));

        Assert.Equal(AgentPermissionDecision.Allow, allowed.Decision);
        Assert.Equal(AgentPermissionDecision.Deny, denied.Decision);
        Assert.Equal(AgentPermissionDecision.Ask, allowed.BaseDecision);
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, allowed.Source);
        Assert.Equal(session.SessionId, allowed.SourceSessionId);
        Assert.Equal(AgentPermissionDecisionSource.ConfiguredOverride, denied.Source);
    }

    [UnixTheory("Docker permission resolution requires supported host bind-mount verification.")]
    [InlineData("glob", "{\"pattern\":\"*.txt\"}")]
    [InlineData("glob", "{\"pattern\":\"*.txt\",\"path\":\".\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\",\"path\":\".\"}")]
    public async Task FilesToolSource_DockerSearchPermission_TreatsDotAsDefaultContainerRoot(string toolId, string argumentsJson)
    {
        using var scope = TestScope.Create();
        using var lifecycle = new DockerContainerLifecycleService();
        var runner = CreateReadyDockerCliRunner(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var target = new DockerExecutionTarget(
            scope.Context,
            configService,
            lifecycle,
            imageCatalogService: null,
            runner,
            new PassThroughDockerMountIdentityVerifier());
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var (workspace, _) = CreateDockerWorkspace(scope);
        var binding = CreateBinding(workspace.WorkspaceId, "docker");
        await configService.SaveConfigAsync(
            binding.BindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh"));

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest(toolId, argumentsJson));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal(".", permission.Path);
        Assert.StartsWith(DockerFileSystemExecutor.ClaimNamespacePrefix, permission.ResourceReference, StringComparison.Ordinal);
        Assert.Single(permission.ResourceClaims);
        Assert.Empty(permission.ResourceCapabilities);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesConfiguredScope_ForSinglePathInsideAllowedRoot()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Add File: inside.txt
                +hello
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal("inside.txt", permission.Path);
        Assert.Equal("apply_patch inside.txt", permission.Summary);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesConfiguredScope_ForMultiplePathsInsideAllowedRoot()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Add File: first.txt
                +first
                *** Add File: nested/second.txt
                +second
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch 2 workspace files", permission.Summary);
        Assert.Equal(2, permission.ResourceReferences.Count);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesOutsideConfiguredScope_WhenAnyPathEscapesWorkspacePaths()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var outsidePath = Path.Combine(scope.RootPath, "outside.txt");
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs($"""
                *** Begin Patch
                *** Add File: inside.txt
                +inside
                *** Add File: {outsidePath}
                +outside
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.OutsideConfiguredScope, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch 2 workspace files", permission.Summary);
        Assert.Equal(2, permission.ResourceReferences.Count);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_UsesUnknown_WhenPatchCannotBeParsed()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new LocalExecutionTarget(scope.Context, configService, shellCatalogService));
        var source = new FilesToolSource(scope.Context);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("not a patch")));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission!.BoundaryId);
        Assert.Null(permission.Path);
        Assert.Equal("apply_patch workspace files", permission.Summary);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_LegacyDeleteMetadataFallsBackToCanonicalBoundary()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var target = new FakeExecutionTarget(string.Empty);
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Delete File: obsolete.txt
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission!.BoundaryId);
        Assert.Equal(["obsolete.txt"], permission.ResourceReferences);
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPermission_PreservesCaseDistinctTargetPaths()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var target = new FakeExecutionTarget(string.Empty);
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", ApplyPatchArgs("""
                *** Begin Patch
                *** Add File: Case.txt
                +upper
                *** Add File: case.txt
                +lower
                *** End Patch
                """)));

        Assert.NotNull(permission);
        Assert.Equal(["Case.txt", "case.txt"], target.ResolvedPaths);
        Assert.Equal(2, permission!.ResourceReferences.Count);
    }

    private sealed class TestPermissionSurface : IAgentPermissionSurface
    {
        public string SurfaceId => "test";

        public string DisplayName => "Test";

        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
            =>
            [
                new("shell.execute", "Execute shell commands", "Run shell commands.",
                [
                    new(AgentPermissionBoundaryIds.SelectedExecutionTarget, "Commands in selected workspace target", "Commands run by the selected execution target.", AgentPermissionDecision.Ask),
                ]),
            ];
    }
}

extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Storage;
using Xunit;
using DockerWorkerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using static Sunder.Package.Agent.Tests.WorkspaceDockerFixture;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class DockerWorkspaceConfigurationTests
{
    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_GetConfig_LeavesImageUnconfigured()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        var config = await configService.GetConfigAsync("workspace:primary-execution-target");

        Assert.Null(config.ImageReference);
        Assert.Equal(DockerExecutionWorkspaceConfigService.BuildContainerName("workspace:primary-execution-target"), config.ContainerName);
        Assert.Equal("/bin/sh", config.ShellPath);
        Assert.Empty(config.PathEntries ?? []);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_GetConfig_UsesBindingScopedDefaultContainerNames()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);

        var first = await configService.GetConfigAsync("workspace-one:primary-execution-target");
        var second = await configService.GetConfigAsync("workspace-two:primary-execution-target");

        Assert.StartsWith("sunder-agent-", first.ContainerName);
        Assert.StartsWith("sunder-agent-", second.ContainerName);
        Assert.NotEqual(first.ContainerName, second.ContainerName);
    }

    [Fact]
    public async Task DockerImageCatalogService_StartsEmpty()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);

        Assert.Empty(await imageCatalog.ListImagesAsync());
        Assert.Empty(await new DockerImageCatalogService(scope.Context).ListImagesAsync());
        Assert.Null((await new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog).GetConfigAsync("workspace:primary-execution-target")).ImageReference);
        Assert.All(
            await scope.Context.Storage.State.ListKeysAsync(),
            key => Assert.True(PackageStorageValidation.IsValidKey(key)));
    }

    [Theory]
    [InlineData("custom")]
    public async Task DockerImageCatalogService_RejectsTaglessImageReferences(string imageReference)
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);

        var exception = await Assert.ThrowsAsync<DockerExecutionDomainException>(() => imageCatalog.AddImageAsync(imageReference));

        Assert.StartsWith("docker.image-reference.", exception.Code, StringComparison.Ordinal);
        Assert.Contains("explicit tag or sha256 digest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await imageCatalog.ListImagesAsync());
    }

    [Fact]
    public async Task DockerImageCatalogService_PullImage_ReportsProgressAndMarksReady()
    {
        using var scope = TestScope.Create();
        var calls = new List<IReadOnlyList<string>>();
        var progressLines = new List<string>();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, progress) =>
        {
            calls.Add(args.ToArray());
            if (args.Count >= 2 && string.Equals(args[0], "pull", StringComparison.Ordinal))
            {
                progress?.Report("pulling layer");
            }

            return Task.FromResult(new DockerCliRunResult(0, "ok", TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.AddImageAsync("custom:1.0");

        var result = await imageCatalog.PullImageAsync("custom:1.0", new DelegateProgress(progressLines.Add));

        Assert.True(result.Success, result.Message);
        Assert.Equal(DockerImageStatus.Ready, (await imageCatalog.ListImagesAsync()).Single(image => image.ImageReference == "custom:1.0").Status);
        Assert.Contains(progressLines, line => line.Contains("pulling layer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calls, args => args.SequenceEqual(["pull", "custom:1.0"]));
        Assert.Contains(calls, args => args.SequenceEqual(["image", "inspect", "custom:1.0"]));
    }

    [Fact]
    public void DockerExecutionConfiguration_Schema_ExposesDockerSettings()
    {
        var fields = DockerExecutionConfiguration.Schema.Sections
            .SelectMany(section => section.Fields)
            .ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("300", fields["docker.timeoutSeconds.default"].DefaultValue);
        Assert.Equal("300", fields["docker.timeoutSeconds.default"].Placeholder);
        Assert.Equal("Docker CLI path", fields[DockerCli.ExecutablePathConfigurationKey].Label);
        Assert.Equal("Auto-detect", fields[DockerCli.ExecutablePathConfigurationKey].Placeholder);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_MountsWorkspacePathsAndShellPath()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var (workspace, hostRoot) = CreateDockerWorkspace(scope, "test");
        var bindingId = "workspace:primary-execution-target";

        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/bash"));

        var config = await configService.GetConfigAsync(bindingId);
        var runtimeConfig = configService.BuildRuntimeConfig(bindingId, ToDockerWorkspace(workspace), config);

        Assert.Equal("/bin/bash", config.ShellPath);
        Assert.NotNull(config.ContainerName);
        Assert.DoesNotContain(':', config.ContainerName!);
        var mount = Assert.Single(configService.ResolveMounts(runtimeConfig));
        Assert.Equal(Path.GetFullPath(hostRoot), mount.HostPath);
        Assert.StartsWith("/workspace/test-", mount.ContainerPath, StringComparison.Ordinal);
        Assert.Equal(mount.ContainerPath, runtimeConfig.DefaultWorkingDirectory);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_UsesWorkspacePathHostRoot()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var hostRoot = Path.Combine(scope.RootPath, "custom-docker-root");
        Directory.CreateDirectory(hostRoot);
        var workspace = CreateWorkspace(hostRoot);
        var bindingId = "workspace:primary-execution-target";

        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh"));

        var config = await configService.GetConfigAsync(bindingId);
        var runtimeConfig = configService.BuildRuntimeConfig(bindingId, ToDockerWorkspace(workspace), config);
        var mount = Assert.Single(configService.ResolveMounts(runtimeConfig));

        Assert.Equal(Path.GetFullPath(hostRoot), mount.HostPath);
        Assert.StartsWith("/workspace/custom-docker-root-", mount.ContainerPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_RejectsWorkspacePathWithComma()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var hostRoot = Path.Combine(scope.RootPath, "docker,workspace");
        var workspace = CreateWorkspace(hostRoot);
        var bindingId = "workspace:primary-execution-target";
        var config = new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh");

        var exception = Assert.Throws<InvalidOperationException>(() => configService.BuildRuntimeConfig(bindingId, ToDockerWorkspace(workspace), config));

        Assert.Contains("cannot contain commas", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerExecutionWorkspaceConfigService_BuildRuntimeConfig_UsesStableAliasesForNestedWorkspacePaths()
    {
        using var scope = TestScope.Create();
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context);
        var outerRoot = Path.Combine(scope.RootPath, "workspace");
        var innerRoot = Path.Combine(outerRoot, "test");
        Directory.CreateDirectory(innerRoot);
        var workspace = CreateWorkspace(outerRoot, innerRoot);
        var bindingId = "workspace:primary-execution-target";

        var runtimeConfig = configService.BuildRuntimeConfig(
            bindingId,
            ToDockerWorkspace(workspace),
            new DockerExecutionWorkspaceConfig("test-image:1.0", null, "/bin/sh"));

        Assert.Equal(2, runtimeConfig.Mounts.Count);
        Assert.Equal(runtimeConfig.Mounts.Select(mount => mount.ContainerPath).Distinct(StringComparer.Ordinal).Count(), runtimeConfig.Mounts.Count);
        Assert.All(runtimeConfig.Mounts, mount => Assert.StartsWith("/workspace/", mount.ContainerPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_UsesOnlyReadyImagesAsSelectOptions()
    {
        using var scope = TestScope.Create();
        var runner = new FakeDockerCliRunner(scope.Context, (args, _, _, _, _) =>
        {
            var image = args.Count >= 3 ? args[2] : string.Empty;
            var exitCode = string.Equals(image, "custom:1.0", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            return Task.FromResult(new DockerCliRunResult(exitCode, string.Empty, TimedOut: false, WasTruncated: false));
        });
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();
        var bindingId = AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId);
        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig("custom:1.0", null, "/bin/sh"));

        var sections = await contributor.GetSectionsAsync(
            CreateDockerEditorContext(workspace, "docker", bindingId));

        var imageField = sections.Single().Fields.Single(field => field.FieldId == "image");
        Assert.Equal(nameof(AgentEditorFieldKind.Select), imageField.Kind.ToString());
        Assert.Equal("custom:1.0", imageField.Value);
        var option = Assert.Single(imageField.Options!);
        Assert.Equal("custom:1.0", option.Value);
        Assert.Equal("custom:1.0", option.Label);
        Assert.Null(option.Description);
        Assert.Contains(imageField.Actions!, action => action.Kind.ToString() == nameof(AgentEditorActionKind.RefreshField)
                                                      && action.ActionId == "refresh-docker-images");
        Assert.DoesNotContain(imageField.Actions!, action => action.Kind.ToString() == nameof(AgentEditorActionKind.OpenPackageSettings));
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_AddsSettingsActionWhenNoReadyImagesExist()
    {
        using var scope = TestScope.Create();
        var runner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var sections = await contributor.GetSectionsAsync(
            CreateDockerEditorContext(
                workspace,
                "docker",
                AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)));

        var imageField = sections.Single().Fields.Single(field => field.FieldId == "image");
        Assert.Empty(imageField.Options!);
        Assert.Contains("Pull at least one", imageField.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(imageField.Actions!, action => action.Kind.ToString() == nameof(AgentEditorActionKind.OpenPackageSettings)
                                                      && action.PackageId == "younics.ai.agent.execution.docker"
                                                      && action.Label == "Open Settings");
        Assert.Contains(imageField.Actions!, action => action.Kind.ToString() == nameof(AgentEditorActionKind.RefreshField)
                                                      && action.ActionId == "refresh-docker-images");
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_SaveRejectsUnconfiguredImage()
    {
        using var scope = TestScope.Create();
        var imageCatalog = new DockerImageCatalogService(scope.Context);
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var result = await contributor.SaveSectionAsync(
            new DockerWorkerModels.CYounics_Ai_Agent_Workspace_Editor_WorkspaceEditorSaveSectionRequest
            {
                Context = CreateDockerEditorContext(
                    workspace,
                    "docker",
                    AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)),
                Request = new DockerWorkerModels.AgentEditorSaveRequest
                {
                    SectionId = "docker-execution-settings",
                    Fields = new Dictionary<string, DockerWorkerModels.AgentEditorFieldValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["image"] = new() { Value = "missing:1.0", Items = null },
                        ["shell-path"] = new() { Value = "/bin/sh", Items = null },
                    },
                },
            });

        Assert.False(result.Success);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DockerExecutionWorkspaceEditorContributor_SaveRejectsConfiguredUnreadyImage()
    {
        using var scope = TestScope.Create();
        var runner = new FakeDockerCliRunner(scope.Context, (_, _, _, _, _) =>
            Task.FromResult(new DockerCliRunResult(1, "No such image", TimedOut: false, WasTruncated: false)));
        var imageCatalog = new DockerImageCatalogService(scope.Context, runner);
        await imageCatalog.AddImageAsync("custom:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);
        var workspace = CreateWorkspace();

        var result = await contributor.SaveSectionAsync(
            new DockerWorkerModels.CYounics_Ai_Agent_Workspace_Editor_WorkspaceEditorSaveSectionRequest
            {
                Context = CreateDockerEditorContext(
                    workspace,
                    "docker",
                    AgentWorkspaceService.BuildPrimaryBindingId(workspace.WorkspaceId)),
                Request = new DockerWorkerModels.AgentEditorSaveRequest
                {
                    SectionId = "docker-execution-settings",
                    Fields = new Dictionary<string, DockerWorkerModels.AgentEditorFieldValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["image"] = new() { Value = "custom:1.0", Items = null },
                        ["shell-path"] = new() { Value = "/bin/sh", Items = null },
                    },
                },
            });

        Assert.False(result.Success);
        Assert.Contains("not ready", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pull it", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DockerWorkerModels.AgentWorkspaceEditorContext CreateDockerEditorContext(
        AgentWorkspaceRecord workspace,
        string targetId,
        string bindingId)
        => new()
        {
            ConfigurationId = bindingId,
            TargetId = targetId,
            Workspace = ToDockerWorkspace(workspace),
        };

    private sealed class DelegateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

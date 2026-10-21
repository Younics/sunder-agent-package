using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias DockerWorker;
extern alias LocalWorker;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using DockerWorkerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using LocalWorkerModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;

public sealed class WorkspaceEditorConfigPreservationTests
{
    [Fact]
    public async Task LocalEditorSave_PreservesPathEntries()
    {
        using var scope = RegressionTestPackageScope.Create();
        const string bindingId = "workspace:local";
        var pathEntries = new[] { Path.Combine(scope.RootPath, "bin") };
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        await configService.SaveConfigAsync(bindingId, new LocalExecutionWorkspaceConfig("bash", pathEntries));
        var contributor = new LocalExecutionWorkspaceEditorContributor(
            configService,
            new LocalShellCatalogService(scope.Context));

        var result = await contributor.SaveSectionAsync(
            new LocalWorkerModels.CYounics_Ai_Agent_Workspace_Editor_WorkspaceEditorSaveSectionRequest
            {
                Context = CreateLocalContext("local", bindingId),
                Request = new LocalWorkerModels.AgentEditorSaveRequest
                {
                    SectionId = "local-execution-settings",
                    Fields = new Dictionary<string, LocalWorkerModels.AgentEditorFieldValue>
                    {
                        ["shell"] = new() { Value = "zsh", Items = null },
                    },
                },
            });

        Assert.True(result.Success);
        var saved = await configService.GetConfigAsync(bindingId);
        Assert.Equal("zsh", saved.SelectedShellId);
        Assert.Equal(pathEntries.Select(Path.GetFullPath), saved.PathEntries);
    }

    [Fact]
    public async Task DockerEditorSave_PreservesContainerNameAndPathEntries()
    {
        using var scope = RegressionTestPackageScope.Create();
        const string bindingId = "workspace:docker";
        var dockerRunner = new ReadyDockerCliRunner(scope.Context);
        var imageCatalog = new DockerImageCatalogService(scope.Context, dockerRunner);
        await imageCatalog.AddImageAsync("test-image:1.0");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        await configService.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig(
                "test-image:1.0",
                "preserved-container",
                "/bin/sh",
                ["/opt/tools", "/usr/local/bin"]));
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);

        var result = await contributor.SaveSectionAsync(
            new DockerWorkerModels.CYounics_Ai_Agent_Workspace_Editor_WorkspaceEditorSaveSectionRequest
            {
                Context = CreateDockerContext("docker", bindingId),
                Request = new DockerWorkerModels.AgentEditorSaveRequest
                {
                    SectionId = "docker-execution-settings",
                    Fields = new Dictionary<string, DockerWorkerModels.AgentEditorFieldValue>
                    {
                        ["image"] = new() { Value = "test-image:1.0", Items = null },
                        ["shell-path"] = new() { Value = "/bin/bash", Items = null },
                    },
                },
            });

        Assert.True(result.Success);
        var saved = await configService.GetConfigAsync(bindingId);
        Assert.Equal("test-image:1.0", saved.ImageReference);
        Assert.Equal("preserved-container", saved.ContainerName);
        Assert.Equal("/bin/bash", saved.ShellPath);
        Assert.Equal(["/opt/tools", "/usr/local/bin"], saved.PathEntries);
    }

    private static LocalWorkerModels.AgentWorkspaceEditorContext CreateLocalContext(string targetId, string bindingId)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = new SunderRpcOffsetDateTime(now.ToString("O"));
        return new()
        {
            ConfigurationId = bindingId,
            TargetId = targetId,
            Workspace = new()
            {
                WorkspaceId = "workspace",
                DisplayName = "Workspace",
                Description = null,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
                Paths = [],
                Documents = [],
            },
        };
    }

    private static DockerWorkerModels.AgentWorkspaceEditorContext CreateDockerContext(string targetId, string bindingId)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = new SunderRpcOffsetDateTime(now.ToString("O"));
        return new()
        {
            ConfigurationId = bindingId,
            TargetId = targetId,
            Workspace = new()
            {
                WorkspaceId = "workspace",
                DisplayName = "Workspace",
                Description = null,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
                Paths = [],
                Documents = [],
            },
        };
    }

    private sealed class ReadyDockerCliRunner(IPackageContext context) : DockerCliRunner(context)
    {
        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult("unix:///var/run/docker.sock");

        protected override Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
            => Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
    }
}

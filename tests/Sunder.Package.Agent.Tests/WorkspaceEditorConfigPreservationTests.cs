using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WorkspaceEditorConfigPreservationTests
{
    [Fact]
    public async Task LocalEditorSave_PreservesPathEntries()
    {
        using var scope = RegressionTestPackageScope.Create();
        const string bindingId = "workspace:local";
        var pathEntries = new[] { Path.Combine(scope.RootPath, "bin") };
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        configService.SaveConfig(bindingId, new LocalExecutionWorkspaceConfig("bash", pathEntries));
        var contributor = new LocalExecutionWorkspaceEditorContributor(
            configService,
            new LocalShellCatalogService(scope.Context));

        var result = await contributor.SaveSectionAsync(
            CreateContext("local", bindingId),
            new AgentEditorSaveRequest(
                "local-execution-settings",
                new Dictionary<string, AgentEditorFieldValue>
                {
                    ["shell"] = new("zsh"),
                }));

        Assert.True(result.Success);
        var saved = configService.GetConfig(bindingId);
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
        imageCatalog.AddImage("test-image:latest");
        var configService = new DockerExecutionWorkspaceConfigService(scope.Context, imageCatalog);
        configService.SaveConfig(
            bindingId,
            new DockerExecutionWorkspaceConfig(
                "test-image:latest",
                "preserved-container",
                "/bin/sh",
                ["/opt/tools", "/usr/local/bin"]));
        var contributor = new DockerExecutionWorkspaceEditorContributor(configService, imageCatalog);

        var result = await contributor.SaveSectionAsync(
            CreateContext("docker", bindingId),
            new AgentEditorSaveRequest(
                "docker-execution-settings",
                new Dictionary<string, AgentEditorFieldValue>
                {
                    ["image"] = new("test-image:latest"),
                    ["shell-path"] = new("/bin/bash"),
                }));

        Assert.True(result.Success);
        var saved = configService.GetConfig(bindingId);
        Assert.Equal("test-image:latest", saved.ImageReference);
        Assert.Equal("preserved-container", saved.ContainerName);
        Assert.Equal("/bin/bash", saved.ShellPath);
        Assert.Equal(["/opt/tools", "/usr/local/bin"], saved.PathEntries);
    }

    private static AgentWorkspaceEditorContext CreateContext(string targetId, string bindingId)
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        return new AgentWorkspaceEditorContext(workspace, targetId, bindingId);
    }

    private sealed class ReadyDockerCliRunner(IPackageContext context) : DockerCliRunner(context)
    {
        public override Task<DockerCliRunResult> RunAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
            => Task.FromResult(new DockerCliRunResult(0, string.Empty, TimedOut: false, WasTruncated: false));
    }
}

extern alias BuilderApp;

using System.Text.Json;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;
using BuilderModels = BuilderApp::Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using BuilderServices = BuilderApp::Sunder.Generated.Packages.Sunder_Package_Agent_Builder_Services;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderSetupServiceTests
{
    [Fact]
    public async Task InstallDotnetSdkAsync_OnPosix_UsesDistroNeutralTarballInstaller()
    {
        var runtime = new CapturingPosixRuntime();
        var service = new BuilderSetupService();

        var message = await service.InstallDotnetSdkAsync(CreateExecution(runtime));

        var installCommand = Assert.Single(runtime.ShellCommands, command => command != "printf '%s' \"$HOME\"");
        Assert.DoesNotContain("dotnet-install.sh", installCommand);
        Assert.DoesNotContain("bash", installCommand);
        Assert.Contains("latest.version", installCommand);
        Assert.Contains("dotnet-sdk-$sdk_version-$rid.tar.gz", installCommand);
        Assert.Contains("/etc/alpine-release", installCommand);
        Assert.Contains("ldd --version", installCommand);
        Assert.Contains("apk add --no-cache", installCommand);
        Assert.Contains("apt-get install -y", installCommand);
        Assert.Contains("zypper --non-interactive install", installCommand);
        Assert.Contains("pacman -Sy --noconfirm --needed", installCommand);
        Assert.Contains("$install_dir/dotnet", installCommand);
        Assert.Equal(["/home/builder/.dotnet", "/home/builder/.dotnet/tools"], runtime.PathEntries);
        Assert.Equal("Installed the .NET SDK into /home/builder/.dotnet.", message);
    }

    private static BuilderWorkspaceExecution CreateExecution(CapturingPosixRuntime runtime)
    {
        var now = new SunderRpcOffsetDateTime(DateTimeOffset.UtcNow.ToString("O"));
        var workspace = new BuilderModels.AgentWorkspaceRecord
        {
            WorkspaceId = "workspace.local",
            DisplayName = "Local workspace",
            Description = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Paths = [],
            Documents = [],
        };
        var binding = new BuilderModels.AgentWorkspaceBindingRecord
        {
            BindingId = "binding.local",
            WorkspaceId = workspace.WorkspaceId,
            ExtensionPointId = "execution-targets",
            ContributionId = "local",
            Role = "primary-execution-target",
            IsEnabled = true,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        return new BuilderWorkspaceExecution(
            workspace,
            binding,
            new BuilderModels.AgentExecutionTargetDescriptor
            {
                TargetId = "local",
                TargetKind = "local",
                DisplayName = "Local",
                Description = null,
                SupportsShell = true,
                SupportsFiles = true,
                Facets = [],
            },
            new BuilderModels.AgentExecutionScopeDescriptor
            {
                DisplayName = "Local",
                WorkspacePaths = ["/home/builder"],
                DefaultWorkingDirectory = "/home/builder",
                PathStyleDescription = null,
            },
            new BuilderModels.AgentExecutionShellDescriptor
            {
                ShellId = "sh",
                DisplayName = "POSIX sh",
                ExecutablePath = "/bin/sh",
                SyntaxKind = "posix-sh",
                Description = "POSIX sh",
            },
            SupportsPathMapping: false,
            SupportsPathEnvironment: true,
            runtime);
    }

    private sealed class CapturingPosixRuntime : BuilderServices.IBuilderRuntimeService
    {
        public List<string> ShellCommands { get; } = [];

        public List<string> PathEntries { get; } = [];

        public ValueTask AddPathEntryAsync(
            string workspaceId,
            string executionPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathEntries.Add(executionPath);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SunderContent> ExecuteShellAsync(
            string workspaceId,
            BuilderModels.AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShellCommands.Add(request.Command);
            var output = request.Command == "printf '%s' \"$HOME\""
                ? "/home/builder"
                : "/home/builder/.dotnet";
            return Content(new BuilderProcessResult(0, output, string.Empty));
        }

        public ValueTask<SunderContent> ExecuteProcessAsync(string workspaceId, BuilderModels.AgentProcessCommandRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderContent> ListWorkspacesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<BuilderModels.AgentExecutionPathMapping> MapToHostPathAsync(string workspaceId, string executionPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderContent> ResolveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static ValueTask<SunderContent> Content<T>(T value)
            => ValueTask.FromResult(SunderContent.FromBytes(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                "application/json", "builder-result.json"));
    }
}

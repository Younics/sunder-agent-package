using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderSetupServiceTests
{
    [Fact]
    public async Task InstallDotnetSdkAsync_OnPosix_UsesDistroNeutralTarballInstaller()
    {
        var target = new CapturingPosixExecutionTarget();
        var service = new BuilderSetupService();

        var message = await service.InstallDotnetSdkAsync(CreateExecution(target));

        var installCommand = Assert.Single(target.ShellCommands, command => command != "printf '%s' \"$HOME\"");
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
        Assert.Equal(["/home/builder/.dotnet", "/home/builder/.dotnet/tools"], target.PathEntries);
        Assert.Equal("Installed the .NET SDK into /home/builder/.dotnet.", message);
    }

    private static BuilderWorkspaceExecution CreateExecution(CapturingPosixExecutionTarget target)
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace.local", "Local workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            "binding.local",
            workspace.WorkspaceId,
            "execution-targets",
            "local",
            "primary-execution-target",
            true,
            0,
            now,
            now);
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        return new BuilderWorkspaceExecution(
            workspace,
            binding,
            target.Descriptor,
            new AgentExecutionScopeDescriptor("Local", ["/home/builder"], "/home/builder"),
            target,
            context,
            new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "POSIX sh"),
            PathMapper: null,
            target);
    }

    private sealed class CapturingPosixExecutionTarget : IAgentExecutionTarget, IAgentExecutionPathEnvironment
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "local",
            "local",
            "Local",
            null,
            SupportsShell: true,
            SupportsFiles: true,
            SupportsSearch: true);

        public List<string> ShellCommands { get; } = [];

        public List<string> PathEntries { get; } = [];

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "POSIX sh"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, Exists: true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ShellCommands.Add(request.Command);
            var output = request.Command == "printf '%s' \"$HOME\""
                ? "/home/builder"
                : "/home/builder/.dotnet";
            return ValueTask.FromResult(new AgentShellCommandResult(0, output));
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

        public ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(PathEntries);

        public ValueTask AddPathEntryAsync(
            AgentExecutionTargetContext context,
            string executionPath,
            CancellationToken cancellationToken = default)
        {
            PathEntries.Add(executionPath);
            return ValueTask.CompletedTask;
        }
    }
}

using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Sunder.Package.Agent.Tools.Shell;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class ProcessExecutionHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-process-hardening-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(BoundedProcessRunner.MaximumTimeoutSeconds + 1)]
    [InlineData(int.MaxValue)]
    public async Task ShellTool_RejectsOutOfRangeTimeoutBeforeInvokingTarget(int timeoutSeconds)
    {
        var target = new RecordingExecutionTarget();
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        var source = new ShellToolSource();
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);

        var result = await source.ExecuteAsync(
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding)
            {
                ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
            },
            new AgentToolRequest("shell", $$"""{"command":"echo unsafe","timeoutSeconds":{{timeoutSeconds}}}"""));

        Assert.True(result.IsError);
        Assert.Equal("shell-arguments-invalid", result.ErrorCode);
        Assert.Equal(0, target.ExecuteCount);
    }

    [Fact]
    public async Task ProcessRunner_ValidatesEveryOptionBeforeStartingProcess()
    {
        Directory.CreateDirectory(_root);
        var cases = new (ProcessRunOptions Options, Action<ProcessStartInfo>? MutateStartInfo)[]
        {
            (new ProcessRunOptions(0, 100), null),
            (new ProcessRunOptions(BoundedProcessRunner.MaximumTimeoutSeconds + 1, 100), null),
            (new ProcessRunOptions(10, 0), null),
            (new ProcessRunOptions(10, BoundedProcessRunner.MaximumOutputLength + 1), null),
            (new ProcessRunOptions(10, 100), startInfo => startInfo.RedirectStandardOutput = false),
            (new ProcessRunOptions(10, 100), startInfo => startInfo.RedirectStandardError = false),
            (new ProcessRunOptions(10, 100, "input"), startInfo => startInfo.RedirectStandardInput = false),
            (new ProcessRunOptions(10, 100), startInfo => startInfo.UseShellExecute = true),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var marker = Path.Combine(_root, $"started-{index}");
            var startInfo = CreateMarkerStartInfo(marker);
            cases[index].MutateStartInfo?.Invoke(startInfo);

            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                BoundedProcessRunner.RunAsync(startInfo, cases[index].Options));
            Assert.False(File.Exists(marker), $"Invalid process option case {index} started the process.");
        }
    }

    [Fact]
    public async Task ProcessRunner_PostStartFailureTerminatesProcessTree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "child-survived");
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("(sleep 2 & sleeper=$!; echo started; wait \"$sleeper\"; touch \"$1\") & wait");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(marker);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BoundedProcessRunner.RunAsync(
            startInfo,
            new ProcessRunOptions(10, 1024, Progress: new ThrowingProgress())));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Post-start failure cleanup took {stopwatch.Elapsed}.");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(marker), "A descendant process survived post-start failure cleanup.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ProcessStartInfo CreateMarkerStartInfo(string marker)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"type nul > \"{marker}\"");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("touch \"$1\"");
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add(marker);
        }

        return startInfo;
    }

    private sealed class ThrowingProgress : IProgress<string>
    {
        public void Report(string value) => throw new InvalidOperationException("Injected progress failure.");
    }

    private sealed class RecordingExecutionTarget : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "test",
            "test",
            "Test",
            null,
            SupportsShell: true,
            SupportsFiles: false);

        public int ExecuteCount { get; private set; }

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("test", "test", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "POSIX sh"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return ValueTask.FromResult(new AgentShellCommandResult(0, "unexpected"));
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

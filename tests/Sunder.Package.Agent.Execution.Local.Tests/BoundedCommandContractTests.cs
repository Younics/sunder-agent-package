using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class BoundedCommandContractTests
{
    public static IEnumerable<object[]> CommandBackends()
    {
        yield return ["local"];
        yield return ["docker"];
    }

    [Theory]
    [MemberData(nameof(CommandBackends))]
    public async Task CommandBackends_BoundOutput(string backend)
    {
        var result = await ExecuteAsync(backend, "yes x | head -c 60000", timeoutSeconds: 5, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.WasTruncated);
        Assert.InRange(result.Output.Length, 1, 51_250);
    }

    [Theory]
    [MemberData(nameof(CommandBackends))]
    public async Task CommandBackends_ReportTimeout(string backend)
    {
        var result = await ExecuteAsync(backend, "sleep 5", timeoutSeconds: 1, CancellationToken.None);

        Assert.Equal(124, result.ExitCode);
        Assert.True(result.TimedOut);
    }

    [Theory]
    [MemberData(nameof(CommandBackends))]
    public async Task CommandBackends_PropagateCallerCancellation(string backend)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ExecuteAsync(backend, "sleep 5", timeoutSeconds: 10, cancellation.Token));
    }

    private static async Task<AgentShellCommandResult> ExecuteAsync(
        string backend,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
        => backend switch
        {
            "local" => await LocalCommandRunner.ExecuteAsync(
                CreateStartInfo(command),
                timeoutSeconds,
                Environment.CurrentDirectory,
                executableResolution: null,
                cancellationToken),
            "docker" => ToShellResult(
                await new ShellDockerCommandExecutor().RunAsync(
                    ["exec", "container", "/bin/sh", "-c", command, "sunder-contract"],
                    timeoutSeconds,
                    cancellationToken),
                Environment.CurrentDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };

    private static ProcessStartInfo CreateStartInfo(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The command contract fixture currently requires a POSIX shell.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static AgentShellCommandResult ToShellResult(DockerCliRunResult result, string workingDirectory)
        => new(result.ExitCode, result.Output, result.TimedOut, workingDirectory, result.WasTruncated);
}

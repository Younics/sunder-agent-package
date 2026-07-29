using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Execution.Docker;

namespace Sunder.Package.Agent.Execution.Local.Tests;

internal sealed class ShellDockerCommandExecutor : IDockerCommandExecutor
{
    private const int MaxOutputLength = 51200;
    private const int DefaultTimeoutSeconds = 5;

    public async Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commandIndex = args.IndexOf("-c");
        if (commandIndex <= 0 || commandIndex + 1 >= args.Count)
        {
            throw new InvalidOperationException("The Docker command did not contain a shell invocation.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = args[commandIndex - 1],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        for (var index = commandIndex + 1; index < args.Count; index++)
        {
            startInfo.ArgumentList.Add(args[index]);
        }

        var run = await BoundedProcessRunner.RunAsync(
            startInfo,
            new ProcessRunOptions(timeoutSeconds, MaxOutputLength, standardInput),
            cancellationToken);
        return new DockerCliRunResult(run.ExitCode, run.CombinedOutput, run.TimedOut, run.WasTruncated);
    }

    public Task<int> ResolveDefaultTimeoutSecondsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultTimeoutSeconds);
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
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
}

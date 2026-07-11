using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Execution.Docker;

namespace Sunder.Package.Agent.Execution.Local.Tests;

internal sealed class ShellDockerCommandExecutor : IDockerCommandExecutor
{
    public int MaxOutputLength { get; init; } = 51200;

    public int DefaultTimeoutSeconds { get; init; } = 5;

    public string? PathEnvironment { get; init; }

    public IReadOnlyList<string>? LastArguments { get; private set; }

    public async Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastArguments = args.ToArray();
        var commandIndex = args.IndexOf("-c");
        if (commandIndex <= 0 || commandIndex + 2 >= args.Count)
        {
            throw new InvalidOperationException("The Docker command did not contain a shell helper invocation.");
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
        if (PathEnvironment is not null)
        {
            startInfo.Environment["PATH"] = PathEnvironment;
        }

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

    public int ResolveDefaultTimeoutSeconds() => DefaultTimeoutSeconds;
}

internal sealed class StubDockerCommandExecutor(Func<CancellationToken, DockerCliRunResult> run) : IDockerCommandExecutor
{
    public Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(run(cancellationToken));
    }

    public int ResolveDefaultTimeoutSeconds() => 1;
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

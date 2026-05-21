using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalCommandRunner
{
    private const int MaxOutputLength = 51200;

    public static async ValueTask<AgentShellCommandResult> ExecuteAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        string workingDirectory,
        ExecutableResolution? executableResolution,
        CancellationToken cancellationToken)
    {
        var run = await BoundedProcessRunner.RunAsync(
            startInfo,
            new ProcessRunOptions(timeoutSeconds, MaxOutputLength),
            cancellationToken);
        if (run.StartException is not null)
        {
            return new AgentShellCommandResult(
                127,
                BuildStartFailureMessage(startInfo, workingDirectory, executableResolution, run.StartException),
                TimedOut: false,
                WorkingDirectory: workingDirectory);
        }

        var output = ProcessOutput.StripAnsiEscapeSequences(run.CombinedOutput);
        var truncatedOutput = ProcessOutput.Truncate(output, MaxOutputLength, out var wasTruncated);
        if (run.TimedOut)
        {
            var timeoutMessage = $"Command timed out after {timeoutSeconds} seconds.";
            var timeoutOutput = string.IsNullOrWhiteSpace(truncatedOutput)
                ? timeoutMessage
                : string.Concat(timeoutMessage, Environment.NewLine, truncatedOutput);
            return new AgentShellCommandResult(
                124,
                timeoutOutput,
                TimedOut: true,
                WorkingDirectory: workingDirectory,
                WasTruncated: run.WasTruncated || wasTruncated);
        }

        return new AgentShellCommandResult(
            run.ExitCode,
            truncatedOutput,
            TimedOut: false,
            WorkingDirectory: workingDirectory,
            WasTruncated: run.WasTruncated || wasTruncated);
    }

    public static void ApplyPathEnvironment(ProcessStartInfo startInfo, IReadOnlyList<string> pathEntries)
    {
        if (pathEntries.Count == 0)
        {
            return;
        }

        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
    }

    private static string BuildStartFailureMessage(
        ProcessStartInfo startInfo,
        string workingDirectory,
        ExecutableResolution? executableResolution,
        Exception exception)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return $"Working directory does not exist: {workingDirectory}. Original error: {exception.Message}";
        }

        if (executableResolution is { IsBareExecutableName: true, WasResolved: false })
        {
            var checkedLocations = executableResolution.CheckedLocations.Count == 0
                ? "(none)"
                : string.Join(", ", executableResolution.CheckedLocations);
            return $"Executable '{executableResolution.OriginalFileName}' was not found. Checked PATH locations: {checkedLocations}. Original error: {exception.Message}";
        }

        if (Path.IsPathRooted(startInfo.FileName) && !File.Exists(startInfo.FileName))
        {
            return $"Executable does not exist: {startInfo.FileName}. Original error: {exception.Message}";
        }

        return exception.Message;
    }
}

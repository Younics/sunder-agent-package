using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public class DockerCliRunner(IPackageContext packageContext)
{
    private const int MaxOutputLength = 51200;

    public virtual async Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IProgress<string>? progress = null)
    {
        ProcessStartInfo startInfo;
        try
        {
            startInfo = await DockerCli.CreateStartInfoAsync(
                packageContext,
                args,
                redirectStandardInput: standardInput is not null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new DockerCliRunResult(127, $"Failed to start Docker CLI: {FormatStartError(ex)}", TimedOut: false, WasTruncated: false);
        }

        var run = await BoundedProcessRunner.RunAsync(
            startInfo,
            new ProcessRunOptions(timeoutSeconds, MaxOutputLength, standardInput, progress),
            cancellationToken).ConfigureAwait(false);
        if (run.StartException is not null)
        {
            return new DockerCliRunResult(127, $"Failed to start Docker CLI: {FormatStartError(run.StartException)}", TimedOut: false, WasTruncated: false);
        }

        if (run.TimedOut)
        {
            var timeoutOutput = ProcessOutput.Truncate(run.CombinedOutput, MaxOutputLength, out var timeoutWasTruncated);
            var timeoutMessage = $"Docker command timed out after {timeoutSeconds} seconds.";
            var output = string.IsNullOrWhiteSpace(timeoutOutput)
                ? timeoutMessage
                : string.Concat(timeoutMessage, Environment.NewLine, timeoutOutput);
            return new DockerCliRunResult(124, output, TimedOut: true, run.WasTruncated || timeoutWasTruncated);
        }

        var truncatedOutput = ProcessOutput.Truncate(run.CombinedOutput, MaxOutputLength, out var wasTruncated);
        return new DockerCliRunResult(run.ExitCode, truncatedOutput, TimedOut: false, run.WasTruncated || wasTruncated);
    }

    private static string FormatStartError(Exception exception)
        => exception.Message.Contains("filename or extension is too long", StringComparison.OrdinalIgnoreCase)
            ? "the generated command line was too long. File content should be streamed through stdin instead of passed as a Docker CLI argument."
            : exception.Message;
}

public sealed record DockerCliRunResult(int ExitCode, string Output, bool TimedOut, bool WasTruncated);

using System.Diagnostics;

namespace Sunder.Package.Agent.Builder;

internal sealed record BuilderProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

internal static class BuilderProcessRunner
{
    public static async Task<BuilderProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new BuilderProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}

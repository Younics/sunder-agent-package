using System.Diagnostics;
using System.Text;

namespace Sunder.Package.Agent.Builder;

public sealed record BuilderLocalProcessRequest(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    /// <summary>Set null explicitly for a watch process whose lifetime is governed by cancellation.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumCapturedCharacters { get; init; } = 1024 * 1024;
}

public sealed record BuilderLocalProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(System.Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(static value => !string.IsNullOrWhiteSpace(value)));
}

public interface IBuilderLocalProcessRunner
{
    Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Argument-list execution without a shell, bounded output, and cancellation of the process tree.</summary>
public sealed class BuilderLocalProcessRunner : IBuilderLocalProcessRunner
{
    public async Task<BuilderLocalProcessResult> RunAsync(BuilderLocalProcessRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(request.ExecutablePath) || !Path.IsPathFullyQualified(request.WorkingDirectory))
            throw new ArgumentException("Process executable and working directory must be absolute paths.");
        if (request.MaximumCapturedCharacters is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "The capture limit must be between 1 and 16 MiB characters.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } timeout) lifetime.CancelAfter(timeout);
        lifetime.Token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(request.ExecutablePath)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in request.Environment)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException($"Could not start {request.ExecutablePath}.");
        process.StandardInput.Close();
        using var cancellation = lifetime.Token.Register(() => Kill(process));
        var output = PumpAsync(process.StandardOutput, request.MaximumCapturedCharacters, progress, lifetime.Token);
        var error = PumpAsync(process.StandardError, request.MaximumCapturedCharacters, progress, lifetime.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(lifetime.Token), output, error).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch
        {
            var timedOut = lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            Kill(process);
            lifetime.Cancel();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            if (timedOut && request.Timeout is not null)
                throw new TimeoutException($"{Path.GetFileName(request.ExecutablePath)} exceeded its {request.Timeout} time limit.");
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task<string> PumpAsync(StreamReader reader, int maximum, IProgress<string>? progress, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            text.Append(buffer, 0, count);
            if (text.Length > maximum) { text.Remove(0, text.Length - maximum); truncated = true; }
            progress?.Report(new string(buffer, 0, count));
        }
        return (truncated ? "[Earlier output truncated]\n" : "") + text;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

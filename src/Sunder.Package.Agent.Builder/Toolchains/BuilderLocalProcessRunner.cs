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
        // Cancellation stops the producer, not observation of its exit/remaining pipes.
        var output = PumpAsync(process.StandardOutput, request.MaximumCapturedCharacters, progress);
        var error = PumpAsync(process.StandardError, request.MaximumCapturedCharacters, progress);
        try
        {
            var exit = process.WaitForExitAsync(lifetime.Token);
            var pipes = Task.WhenAll(output, error);
            if (await Task.WhenAny(exit, pipes).ConfigureAwait(false) == pipes) await pipes.ConfigureAwait(false);
            await exit.ConfigureAwait(false);
            await pipes.ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception failure)
        {
            var expectedCancellation = failure is OperationCanceledException cancelled
                && cancelled.CancellationToken == lifetime.Token && lifetime.IsCancellationRequested;
            var timedOut = expectedCancellation && !cancellationToken.IsCancellationRequested;
            Kill(process);
            var drain = Task.WhenAll(process.WaitForExitAsync(), output, error);
            try { await drain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) when (!drain.IsCompleted)
            {
                // Five seconds is the cleanup grace, not proof of termination. Keep the
                // actual driver, project lock and SDK lease owned until it really drains.
                try { progress?.Report("Process shutdown is unconfirmed; retaining project ownership while cleanup drains.\n"); }
                catch { /* A notification cannot release an unproven producer. */ }
                await drain.ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (process.HasExited && ReferenceEquals(cleanupFailure, failure)) { }
            if (timedOut && request.Timeout is not null)
                throw new TimeoutException($"{Path.GetFileName(request.ExecutablePath)} exceeded its {request.Timeout} time limit.", failure);
            if (expectedCancellation) cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task<string> PumpAsync(StreamReader reader, int maximum, IProgress<string>? progress)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
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

using System.Diagnostics;
using System.Text;

namespace Sunder.Agent.Execution.Common;

public sealed record ProcessRunOptions(
    int TimeoutSeconds,
    int MaxOutputLength,
    string? StandardInput = null,
    IProgress<string>? Progress = null);

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool WasTruncated,
    Exception? StartException = null)
{
    public bool StartFailed => StartException is not null;

    public string CombinedOutput => string.Concat(StandardOutput, StandardError);
}

public static class BoundedProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        ProcessRunOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessRunResult(127, string.Empty, string.Empty, TimedOut: false, WasTruncated: false, StartException: ex);
        }

        var stdoutTask = ReadToEndBoundedAsync(process.StandardOutput, options.MaxOutputLength, options.Progress, cancellationToken);
        var stderrTask = ReadToEndBoundedAsync(process.StandardError, options.MaxOutputLength, options.Progress, cancellationToken);
        var stdinTask = WriteStandardInputAsync(process, options.StandardInput, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await stdinTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await ObserveTaskAsync(stdinTask).ConfigureAwait(false);
            var timeoutStdout = await CompleteOutputTaskAsync(stdoutTask).ConfigureAwait(false);
            var timeoutStderr = await CompleteOutputTaskAsync(stderrTask).ConfigureAwait(false);
            return new ProcessRunResult(
                124,
                timeoutStdout.Content,
                timeoutStderr.Content,
                TimedOut: true,
                timeoutStdout.WasTruncated || timeoutStderr.WasTruncated);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            ObserveFaults(stdoutTask);
            ObserveFaults(stderrTask);
            ObserveFaults(stdinTask);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessRunResult(
            process.ExitCode,
            stdout.Content,
            stderr.Content,
            TimedOut: false,
            stdout.WasTruncated || stderr.WasTruncated);
    }

    private static async Task<BoundedProcessOutput> ReadToEndBoundedAsync(
        StreamReader reader,
        int maxLength,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder(capacity: Math.Min(maxLength, buffer.Length));
        var wasTruncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (progress is not null)
            {
                var progressLine = NormalizeProgressChunk(new string(buffer, 0, read));
                if (!string.IsNullOrWhiteSpace(progressLine))
                {
                    progress.Report(progressLine);
                }
            }

            var remaining = maxLength - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                wasTruncated = true;
            }
        }

        return new BoundedProcessOutput(builder.ToString(), wasTruncated);
    }

    private static async Task WriteStandardInputAsync(Process process, string? standardInput, CancellationToken cancellationToken)
    {
        if (standardInput is null)
        {
            return;
        }

        try
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is IOException or InvalidOperationException) && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }
    }

    private static string NormalizeProgressChunk(string chunk)
        => chunk.Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<BoundedProcessOutput> CompleteOutputTaskAsync(Task<BoundedProcessOutput> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            ObserveFaults(task);
            return new BoundedProcessOutput(string.Empty, WasTruncated: false);
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            ObserveFaults(task);
        }
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record BoundedProcessOutput(string Content, bool WasTruncated);
}

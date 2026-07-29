using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public const int MaximumTimeoutSeconds = 86_400;
    public const int MaximumOutputLength = 10 * 1024 * 1024;
    private const int MaximumProcessTreeSize = 4096;

    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        ProcessRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(startInfo, options);
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

        Task<BoundedProcessOutput>? stdoutTask = null;
        Task<BoundedProcessOutput>? stderrTask = null;
        Task? stdinTask = null;
        CancellationTokenSource? timeoutCts = null;

        try
        {
            stdoutTask = ReadToEndBoundedAsync(process.StandardOutput, options.MaxOutputLength, options.Progress, cancellationToken);
            stderrTask = ReadToEndBoundedAsync(process.StandardError, options.MaxOutputLength, options.Progress, cancellationToken);
            stdinTask = WriteStandardInputAsync(process, options.StandardInput, cancellationToken);
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var exitTask = process.WaitForExitAsync(timeoutCts.Token);
            await WaitForOperationsAsync(exitTask, stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
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
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            ObserveFaults(stdoutTask);
            ObserveFaults(stderrTask);
            ObserveFaults(stdinTask);
            throw;
        }
        catch
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            ObserveFaults(stdoutTask);
            ObserveFaults(stderrTask);
            ObserveFaults(stdinTask);
            throw;
        }
        finally
        {
            timeoutCts?.Dispose();
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

    private static void ValidateOptions(ProcessStartInfo startInfo, ProcessRunOptions options)
    {
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ArgumentException("Process file name cannot be empty.", nameof(startInfo));
        }

        if (options.TimeoutSeconds is <= 0 or > MaximumTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.TimeoutSeconds, $"Process timeout must be between 1 and {MaximumTimeoutSeconds} seconds.");
        }

        if (options.MaxOutputLength is <= 0 or > MaximumOutputLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxOutputLength, $"Maximum output length must be between 1 and {MaximumOutputLength} characters.");
        }

        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException("Bounded process execution requires UseShellExecute to be false.", nameof(startInfo));
        }

        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException("Bounded process execution requires redirected standard output and standard error.", nameof(startInfo));
        }

        if (options.StandardInput is not null && !startInfo.RedirectStandardInput)
        {
            throw new ArgumentException("Standard input must be redirected when input content is provided.", nameof(startInfo));
        }
    }

    private static async Task WaitForOperationsAsync(params Task[] operations)
    {
        var pending = operations.ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            pending.Remove(completed);
        }
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
                if (!TryKillUnixProcessTree(process.Id))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryKillUnixProcessTree(int rootProcessId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var processIds = new List<int> { rootProcessId };
            var knownProcessIds = new HashSet<int> { rootProcessId };
            var stablePasses = 0;
            for (var pass = 0; pass < 16 && stablePasses < 2; pass++)
            {
                foreach (var processId in processIds)
                {
                    _ = KillUnixProcess(processId, GetUnixStopSignal());
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(5));
                var added = false;
                for (var index = 0; index < processIds.Count; index++)
                {
                    if (!TryGetUnixChildProcessIds(processIds[index], out var childProcessIds))
                    {
                        return false;
                    }
                    foreach (var childProcessId in childProcessIds)
                    {
                        if (childProcessId <= 0 || !knownProcessIds.Add(childProcessId))
                        {
                            continue;
                        }
                        if (processIds.Count >= MaximumProcessTreeSize)
                        {
                            return false;
                        }
                        processIds.Add(childProcessId);
                        added = true;
                    }
                }

                stablePasses = added ? 0 : stablePasses + 1;
            }

            for (var index = processIds.Count - 1; index >= 0; index--)
            {
                _ = KillUnixProcess(processIds[index], 9);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or FormatException
                                   or OverflowException
                                   or DllNotFoundException
                                   or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetUnixChildProcessIds(int processId, out IReadOnlyList<int> childProcessIds)
    {
        if (OperatingSystem.IsMacOS())
        {
            var count = ListMacChildProcesses(processId, null, 0);
            if (count < 0)
            {
                childProcessIds = [];
                return false;
            }
            if (count == 0)
            {
                childProcessIds = [];
                return true;
            }

            var buffer = new int[Math.Min(count + 16, MaximumProcessTreeSize)];
            var result = ListMacChildProcesses(processId, buffer, checked(buffer.Length * sizeof(int)));
            if (result < 0)
            {
                childProcessIds = [];
                return false;
            }
            var resultCount = result <= buffer.Length ? result : result / sizeof(int);
            if (resultCount > buffer.Length)
            {
                childProcessIds = [];
                return false;
            }
            childProcessIds = buffer[..resultCount];
            return true;
        }

        var childrenPath = $"/proc/{processId}/task/{processId}/children";
        try
        {
            childProcessIds = File.ReadAllText(childrenPath)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            return true;
        }
        catch (FileNotFoundException)
        {
            childProcessIds = [];
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            childProcessIds = [];
            return true;
        }
    }

    private static int GetUnixStopSignal() => OperatingSystem.IsMacOS() ? 17 : 19;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillUnixProcess(int processId, int signal);

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listchildpids", SetLastError = true)]
    private static extern int ListMacChildProcesses(int parentProcessId, [Out] int[]? buffer, int bufferSize);

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

    private static async Task<BoundedProcessOutput> CompleteOutputTaskAsync(Task<BoundedProcessOutput>? task)
    {
        if (task is null)
        {
            return new BoundedProcessOutput(string.Empty, WasTruncated: false);
        }

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

    private static async Task ObserveTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            ObserveFaults(task);
        }
    }

    private static void ObserveFaults(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record BoundedProcessOutput(string Content, bool WasTruncated);
}

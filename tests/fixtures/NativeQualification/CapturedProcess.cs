using System.Diagnostics;
using System.Text;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

internal sealed class CapturedProcess : IAsyncDisposable
{
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly object sync = new();

    private CapturedProcess(Process process, int limit)
    {
        Process = process;
        process.OutputDataReceived += (_, line) => Capture(stdout, line.Data);
        process.ErrorDataReceived += (_, line) => Capture(stderr, line.Data);

        void Capture(StringBuilder output, string? line)
        {
            if (line is null) return;
            lock (sync)
            {
                output.Append(line).Append('\n');
                if (output.Length > limit) output.Remove(0, output.Length - limit);
            }
        }
    }

    internal Process Process { get; }
    internal int Id => Process.Id;
    internal string Diagnostics
    {
        get
        {
            lock (sync) return $"PID {Id}; exited: {Process.HasExited}\nstdout:\n{stdout}\nstderr:\n{stderr}";
        }
    }

    internal static CapturedProcess Start(ProcessStartInfo start, int maximumOutputCharacters, string diagnosticsDirectory)
    {
        Directory.CreateDirectory(diagnosticsDirectory);
        File.WriteAllText(Path.Combine(diagnosticsDirectory, "command.txt"), start.FileName);
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        var process = new Process { StartInfo = start };
        var capture = new CapturedProcess(process, maximumOutputCharacters);
        Assert.True(process.Start(), $"Could not start {start.FileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return capture;
    }

    internal async Task WaitForOutputAsync(Func<string, bool> predicate, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                lock (sync) if (predicate(stdout.ToString())) return;
                if (Process.HasExited)
                {
                    // An exited child may still have undrained output events.
                    await Process.WaitForExitAsync(deadline.Token);
                    lock (sync) Assert.True(predicate(stdout.ToString()), Diagnostics);
                    return;
                }
                await Task.Delay(25, deadline.Token);
            }
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"App did not produce its expected output: {Diagnostics}", error);
        }
    }

    internal async Task<(string StandardOutput, string StandardError)> EnsureSuccessAsync(TimeSpan timeout, CancellationToken token = default)
    {
        try { await Process.WaitForExitAsync().WaitAsync(timeout, token); }
        catch (TimeoutException error) { throw new TimeoutException(Diagnostics, error); }
        Assert.True(Process.ExitCode == 0, $"App exited with {Process.ExitCode}: {Diagnostics}");
        lock (sync) return (stdout.ToString(), stderr.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { Process.Dispose(); }
    }
}

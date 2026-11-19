using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Sunder.Package.Agent.TestSupport;

// Diagnostic-branch observer only. It never queues pool work, changes thread
// limits, delays a test gate, replaces a dispatcher, or supplies test responses.
public sealed class InterferenceTrace : IDisposable
{
    private readonly string? _path;
    private readonly string _name;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly ConcurrentQueue<object> _events = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Func<object?>? _state;
    private readonly Thread? _sampler;
    private int _disposed;

    public InterferenceTrace(string name, Func<object?>? state = null)
    {
        _name = name;
        _state = state;
        if (Environment.GetEnvironmentVariable("SUNDER_TEST_INTERFERENCE_TRACE") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, $"{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
        Record("observer-start", new
        {
            name,
            processId = Environment.ProcessId,
            processors = Environment.ProcessorCount,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            context = SynchronizationContext.Current?.GetType().FullName,
        });
        _sampler = new Thread(Sample) { IsBackground = true, Name = "Sunder diagnostic observer" };
        _sampler.Start();
    }

    public void Record(string stage, object? details = null)
    {
        if (_path is null) return;
        _events.Enqueue(new
        {
            name = _name,
            stage,
            elapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            utc = DateTimeOffset.UtcNow,
            thread = Environment.CurrentManagedThreadId,
            poolThread = Thread.CurrentThread.IsThreadPoolThread,
            details,
        });
    }

    private void Sample()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            do
            {
                ThreadPool.GetAvailableThreads(out var available, out _);
                ThreadPool.GetMinThreads(out var minimum, out _);
                ThreadPool.GetMaxThreads(out var maximum, out _);
                Record("sample", new
                {
                    poolThreads = ThreadPool.ThreadCount,
                    queued = ThreadPool.PendingWorkItemCount,
                    completed = ThreadPool.CompletedWorkItemCount,
                    lockContentions = Monitor.LockContentionCount,
                    available,
                    minimum,
                    maximum,
                    cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                    allocated = GC.GetTotalAllocatedBytes(),
                    gen0 = GC.CollectionCount(0),
                    gen1 = GC.CollectionCount(1),
                    gen2 = GC.CollectionCount(2),
                    gcPausePercent = GC.GetGCMemoryInfo().PauseTimePercentage,
                    state = _state?.Invoke(),
                });
            } while (!_stop.Wait(20));
        }
        catch (Exception exception) { Record("observer-error", exception.ToString()); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        _sampler?.Join();
        if (_path is not null)
        {
            Record("observer-stop");
            using var writer = new StreamWriter(_path);
            foreach (var item in _events) writer.WriteLine(JsonSerializer.Serialize(item));
        }
        _stop.Dispose();
    }
}

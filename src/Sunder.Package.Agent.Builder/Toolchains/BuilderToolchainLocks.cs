using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderToolchainLocks
{
    private sealed class Gate
    {
        internal SemaphoreSlim Operations { get; } = new(1, 1);
        internal int Leases;
    }

    private static readonly ConcurrentDictionary<string, Gate> Gates = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal sealed class Operation : IDisposable
    {
        private readonly Gate gate;
        private readonly FileStream processLock;
        private readonly string usePath;
        private int disposed;

        private Operation(Gate gate, FileStream processLock, string usePath)
        {
            this.gate = gate;
            this.processLock = processLock;
            this.usePath = usePath;
        }

        internal IDisposable BeginMutation()
        {
            if (Volatile.Read(ref gate.Leases) != 0) throw InUse();
            BuilderToolchainFiles.RejectLink(usePath);
            try { return new FileStream(usePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw InUse(ex); }
        }

        internal Action BeginSelection()
        {
            BuilderToolchainFiles.RejectLink(usePath);
            if (!File.Exists(usePath))
            {
                using var create = new FileStream(usePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            var stream = new FileStream(usePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Interlocked.Increment(ref gate.Leases);
            var released = 0;
            return () =>
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                stream.Dispose();
                Interlocked.Decrement(ref gate.Leases);
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            processLock.Dispose();
            gate.Operations.Release();
        }

        internal static async Task<Operation> AcquireAsync(string path, CancellationToken token)
        {
            var gate = Gates.GetOrAdd(path, static _ => new());
            if (!await gate.Operations.WaitAsync(TimeSpan.FromMinutes(20), token).ConfigureAwait(false))
                throw new TimeoutException("Another toolchain operation is still running. Retry after it completes.");
            try
            {
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    BuilderToolchainFiles.RejectLink(path);
                    try
                    {
                        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        return new(gate, stream, path + ".use");
                    }
                    catch (IOException) when (timer.Elapsed < TimeSpan.FromMinutes(20))
                    {
                        await Task.Delay(150, token).ConfigureAwait(false);
                    }
                }
            }
            catch { gate.Operations.Release(); throw; }
        }
    }

    private static BuilderToolchainException InUse(Exception? inner = null) => new("selection",
        "This toolchain is selected by an active build or another process. Stop that operation before repair or removal.", true, inner);
}

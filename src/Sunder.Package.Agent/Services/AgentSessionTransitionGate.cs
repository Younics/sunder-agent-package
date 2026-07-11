using System.Collections.Concurrent;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionTransitionGate
{
    internal static AgentSessionTransitionGate Shared { get; } = new();

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    internal async ValueTask<IDisposable> EnterAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

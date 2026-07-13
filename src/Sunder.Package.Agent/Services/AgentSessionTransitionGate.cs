using Sunder.Package.Agent.Shared.Threading;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionTransitionGate
{
    internal static AgentSessionTransitionGate Shared { get; } = new();

    private readonly ReferenceCountedKeyedLock<Guid> _gates = new();

    internal int GateCount => _gates.Count;

    internal async ValueTask<IDisposable> EnterAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => await _gates.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false);
}

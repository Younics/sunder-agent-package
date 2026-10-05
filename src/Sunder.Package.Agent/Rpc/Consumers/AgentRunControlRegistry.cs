using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

public sealed class AgentRunControlRegistry
{
    private readonly ConcurrentDictionary<Guid, Registration> _registrations = new();

    public IDisposable Open(IAgentBehaviorLoopRuntime runtime, string callerPackageId, out Guid invocationId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerPackageId);
        do invocationId = Guid.NewGuid();
        while (!_registrations.TryAdd(invocationId, new Registration(runtime, callerPackageId)));
        return new RegistrationLease(_registrations, invocationId);
    }

    internal IAgentBehaviorLoopRuntime Resolve(Guid invocationId, string callerPackageId)
    {
        if (!_registrations.TryGetValue(invocationId, out var registration)
            || !string.Equals(registration.CallerPackageId, callerPackageId, StringComparison.Ordinal))
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.PermissionDenied,
                "agent.run-control.invalid-invocation",
                "The behavior-loop run-control invocation is unavailable."));
        }
        return registration.Runtime;
    }

    private sealed record Registration(IAgentBehaviorLoopRuntime Runtime, string CallerPackageId);

    private sealed class RegistrationLease(
        ConcurrentDictionary<Guid, Registration> registrations,
        Guid invocationId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) registrations.TryRemove(invocationId, out _);
        }
    }
}

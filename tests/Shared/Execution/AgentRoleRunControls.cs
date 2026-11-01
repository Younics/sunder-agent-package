using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Tests;

// A test session/run around the real domain registry in a loaded Worker role.
// This creates no RPC registration or alternate invocation authority.
internal sealed class AgentRoleRunControls(Assembly role, object? registry = null)
{
    internal object Value { get; } = registry ?? Activator.CreateInstance(role.GetType("Sunder.Package.Agent.Services.AgentActiveRunRegistry", true)!)!;

    internal IDisposable Open(IAgentBehaviorLoopRuntime runtime, string caller, out Guid invocation)
    {
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        // An explicitly supplied production registry can have a different load
        // context than the role used for assertion metadata.
        var handleType = Value.GetType().Assembly.GetType("Sunder.Package.Agent.Services.AgentActiveRunHandle", true)!;
        var handle = AgentRoleBoundary.Create(handleType, runId, 1L, DateTimeOffset.UtcNow, "fixture.profile", "Fixture run", cancellation);
        AgentRoleBoundary.Call(Value, "Activate", sessionId, handle);
        var grant = AgentRoleBoundary.Call(Value, "OpenBehaviorInvocation", sessionId, runId, 1L, runtime, caller)!;
        invocation = (Guid)grant.GetType().GetProperty("InvocationId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(grant)!;
        return new RunLifetime(Value, sessionId, runId, (IDisposable)grant, cancellation);
    }

    private sealed class RunLifetime(object registry, Guid sessionId, Guid runId, IDisposable grant, CancellationTokenSource cancellation) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                grant.Dispose();
                AgentRoleBoundary.Call(registry, "Complete", sessionId, runId, 1L);
            }
            finally { cancellation.Dispose(); }
        }
    }
}

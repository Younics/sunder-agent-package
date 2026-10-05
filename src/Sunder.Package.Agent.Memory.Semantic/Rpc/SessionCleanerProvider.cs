using Sunder.Generated.Rpc;
using Sunder.Generated.Rpc.Imports.CSunder_Agent_Session_Cleaner;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

[SunderRpcProvider(typeof(ISessionCleanerService), ProviderId = "semantic.memory.session.cleaner")]
public sealed partial class SessionCleanerProvider(string cleanerId, Action<Guid> deleteSession) : ISessionCleanerService
{
    public ValueTask<AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Unit request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentRpcIdentity { Id = cleanerId, DisplayName = cleanerId });
    }

    public ValueTask<Unit> DeleteSessionAsync(SunderRpcInvocationContext context, SessionIdRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The existing synchronous cleaner drains the durable deletion before return.
        deleteSession(request.SessionId);
        return ValueTask.FromResult(new Unit());
    }
}

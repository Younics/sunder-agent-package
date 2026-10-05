using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.session.cleaner",
    ServiceId = "session-cleaner", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.attachment.cleaner")]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public partial class SessionCleanerService(IAgentSessionDataCleaner cleaner)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentRpcIdentity> DescribeAsync(Unit request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentRpcIdentity { DisplayName = cleaner.CleanerId, Id = cleaner.CleanerId });
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<Unit> DeleteSessionAsync(SessionIdRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Existing cleaners own synchronous/drained deletion. Cancellation may
        // prevent entry, but must not abandon an already-started cleanup operation.
        cleaner.DeleteSessionData(request.SessionId.Value);
        return ValueTask.FromResult(new Unit());
    }
}

[SunderRpcProvider(typeof(SessionCleanerService), ProviderId = "agent.history.cleaner")]
public sealed partial class HistorySessionCleanerProvider(IAgentSessionDataCleaner cleaner) : SessionCleanerService(cleaner);

public sealed class SessionIdRequest
{
    public required EntityId SessionId { get; init; }
}

using Sunder.Generated.Rpc;
using Sunder.Generated.Rpc.Imports.CSunder_Agent_Session_Cleaner;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI.Rpc;

[SunderRpcProvider(typeof(ISessionCleanerService), ProviderId = "openai.continuation.cleaner")]
internal sealed partial class SessionCleanerProvider(IAgentSessionDataCleaner cleaner) : ISessionCleanerService
{
    public ValueTask<AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentRpcIdentity { Id = cleaner.CleanerId, DisplayName = cleaner.CleanerId }); }
    public ValueTask<Unit> DeleteSessionAsync(SunderRpcInvocationContext context, SessionIdRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); cleaner.DeleteSessionData(request.SessionId); return ValueTask.FromResult(new Unit()); }
}

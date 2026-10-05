using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Session_Cleaner;

namespace Sunder.Package.Agent.Mcp.Rpc;

[SunderRpcProvider(typeof(ISessionCleanerService), ProviderId = "mcp.session.cleaner")]
public sealed partial class SessionCleanerProvider(IAgentSessionDataCleaner cleaner) : ISessionCleanerService
{
    public ValueTask<Wire.AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.AgentRpcIdentity { Id = cleaner.CleanerId, DisplayName = cleaner.CleanerId }); }
    public ValueTask<Wire.Unit> DeleteSessionAsync(SunderRpcInvocationContext context, Wire.SessionIdRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); cleaner.DeleteSessionData(request.SessionId); return ValueTask.FromResult(new Wire.Unit()); }
}

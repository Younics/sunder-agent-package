using Sunder.Generated.Rpc;
using Sunder.Generated.Rpc.Imports.CSunder_Agent_Session_Cleaner;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

[SunderRpcProvider(typeof(ISessionCleanerService), ProviderId = "files.session.cleaner")]
internal sealed partial class SessionCleanerProvider(FilesToolSource source) : ISessionCleanerService
{
    public ValueTask<AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentRpcIdentity { Id = source.CleanerId, DisplayName = source.CleanerId }); }
    public ValueTask<Unit> DeleteSessionAsync(SunderRpcInvocationContext context, SessionIdRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); source.DeleteSessionData(request.SessionId); return ValueTask.FromResult(new Unit()); }
}

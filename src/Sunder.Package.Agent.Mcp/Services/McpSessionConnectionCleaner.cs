using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Mcp.Services;

[SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "mcp.session.cleaner")]
internal sealed partial class McpSessionConnectionCleaner(McpClientConnectionManager connections) : ISessionCleanerService
{
    public string CleanerId => "agent.mcp.connections";

    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = CleanerId, DisplayName = CleanerId }); }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); await connections.DisconnectSessionAsync(sessionId).ConfigureAwait(false); }
}

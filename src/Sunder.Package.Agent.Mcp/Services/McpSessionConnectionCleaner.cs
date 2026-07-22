using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed class McpSessionConnectionCleaner(McpClientConnectionManager connections)
    : IAgentSessionDataCleaner
{
    public string CleanerId => "agent.mcp.connections";

    public void DeleteSessionData(Guid sessionId)
        => connections.DisconnectSessionAsync(sessionId).GetAwaiter().GetResult();
}

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpOAuthCoordinator(
    McpOAuthService? oauthService,
    McpClientConnectionManager connections)
{
    public async Task ClearAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = oauthService ?? throw new InvalidOperationException("MCP OAuth is unavailable.");
        await service.ClearAuthorizationAsync(server.ServerId, cancellationToken);
        await connections.DisconnectServerAsync(server.ServerId).ConfigureAwait(false);
    }
}

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpOAuthCoordinator(
    McpOAuthService? oauthService,
    McpClientConnectionManager connections)
{
    public async Task AuthorizeAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
    {
        var service = oauthService ?? throw new InvalidOperationException("MCP OAuth is unavailable.");
        await service.AuthorizeAsync(
            server,
            McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server),
            cancellationToken).ConfigureAwait(false);
        await connections.DisconnectServerAsync(server.ServerId).ConfigureAwait(false);
    }

    public async Task ClearAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = oauthService ?? throw new InvalidOperationException("MCP OAuth is unavailable.");
        service.ClearAuthorization(server.ServerId);
        await connections.DisconnectServerAsync(server.ServerId).ConfigureAwait(false);
    }
}

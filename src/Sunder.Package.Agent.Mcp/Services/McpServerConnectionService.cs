using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed record McpConnectionPresentation(McpConnectionStatus Status, string Detail, string Diagnostics);

public sealed class McpServerConnectionService(
    McpServerCatalogService catalog,
    McpClientConnectionManager connections,
    McpOAuthService? oauthService)
{
    internal event Action? StatusChanged
    {
        add => connections.StatusChanged += value;
        remove => connections.StatusChanged -= value;
    }

    internal async Task<IReadOnlyList<McpClientTool>> DiscoverAsync(
        ConfiguredMcpServerRecord server,
        bool reconnect,
        CancellationToken cancellationToken)
    {
        if (reconnect)
        {
            await connections.DisconnectServerAsync(server.ServerId).ConfigureAwait(false);
        }

        return await connections.GetToolsAsync(
            server,
            await catalog.GetHeadersAsync(server, cancellationToken),
            await catalog.GetEnvironmentVariablesAsync(server, cancellationToken),
            McpTimeoutResolver.ResolveDiscoveryTimeoutMilliseconds(server),
            cancellationToken).ConfigureAwait(false);
    }

    internal Task DisconnectAsync(string serverId) => connections.DisconnectServerAsync(serverId);

    internal async Task<McpConnectionPresentation> GetPresentationAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken = default)
    {
        var status = connections.GetStatus(server);
        var oauth = server.OAuthEnabled
            ? oauthService is not null && await oauthService.HasCachedAuthorizationAsync(server.ServerId, cancellationToken)
                ? "Cached" : "Required"
            : null;
        var detail = new List<string>
        {
            $"Active connection: {(status.ActiveConnectionCount > 0 ? "Yes" : "No")}",
            $"Discovered tools: {status.ToolCount ?? 0}",
        };
        if (oauth is not null)
        {
            detail.Add($"OAuth authorization: {oauth}");
        }

        List<string> diagnostics =
        [
            $"Status: {status.Kind}",
            $"Message: {status.Message}",
            .. detail,
        ];
        if (status.LastChangedAtUtc is not null)
        {
            diagnostics.Add($"Updated: {status.LastChangedAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC");
        }

        AddSection(diagnostics, "Tools:", status.ToolNames?.Select(name => "- " + name));
        AddSection(diagnostics, "Error:", string.IsNullOrWhiteSpace(status.Error) ? null : [status.Error]);
        AddSection(diagnostics, "stderr:", status.StandardErrorTail);
        return new McpConnectionPresentation(status, string.Join(Environment.NewLine, detail), string.Join(Environment.NewLine, diagnostics));
    }

    private static void AddSection(ICollection<string> lines, string heading, IEnumerable<string>? values)
    {
        var items = values?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        if (items.Length == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add(heading);
        foreach (var item in items)
        {
            lines.Add(item);
        }
    }
}

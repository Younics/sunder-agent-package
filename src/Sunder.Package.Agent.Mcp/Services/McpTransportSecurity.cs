namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpTransportSecurity
{
    public static void ValidateRemoteEndpoint(
        ConfiguredMcpServerRecord server,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (server.TransportType != ConfiguredMcpTransportType.HttpSse)
        {
            return;
        }

        if (!Uri.TryCreate(server.EndpointUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Remote MCP endpoints must use an absolute HTTP or HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(endpoint.UserInfo))
        {
            throw new InvalidOperationException("Remote MCP endpoint URLs must not contain user-info credentials. Use secret-backed headers or OAuth over HTTPS.");
        }

        var hasCredentials = server.OAuthEnabled
                             || server.HeaderNames.Length > 0
                             || headers is { Count: > 0 }
                             || !string.IsNullOrEmpty(endpoint.Query);
        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !endpoint.IsLoopback
            && hasCredentials)
        {
            throw new InvalidOperationException("Non-loopback MCP endpoints that use credentials must use HTTPS.");
        }
    }
}

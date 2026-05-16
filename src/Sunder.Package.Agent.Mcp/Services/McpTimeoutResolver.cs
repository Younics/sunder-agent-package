namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpTimeoutResolver
{
    public static int? ResolveDiscoveryTimeoutMilliseconds(ConfiguredMcpServerRecord server)
        => ResolveOptionalTimeoutMilliseconds(server.DiscoveryTimeoutMilliseconds ?? server.TimeoutMilliseconds);

    public static int? ResolveToolTimeoutMilliseconds(ConfiguredMcpServerRecord server)
        => ResolveOptionalTimeoutMilliseconds(server.ToolTimeoutMilliseconds ?? server.TimeoutMilliseconds);

    public static int? ResolveEffectiveTimeoutMilliseconds(int? serverTimeoutMilliseconds)
        => ResolveOptionalTimeoutMilliseconds(serverTimeoutMilliseconds);

    public static int? ResolveBackgroundRefreshTimeoutMilliseconds(int? discoveryTimeoutMilliseconds)
        => ResolveOptionalTimeoutMilliseconds(discoveryTimeoutMilliseconds);

    private static int? ResolveOptionalTimeoutMilliseconds(int? timeoutMilliseconds)
        => timeoutMilliseconds is > 0 ? timeoutMilliseconds.Value : null;
}

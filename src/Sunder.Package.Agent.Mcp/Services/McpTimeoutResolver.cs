namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpTimeoutResolver
{
    internal const int DefaultDiscoveryTimeoutMilliseconds = 15_000;
    internal const int DefaultToolTimeoutMilliseconds = 120_000;
    internal const int MaximumDiscoveryTimeoutMilliseconds = 120_000;
    internal const int MaximumToolTimeoutMilliseconds = 1_800_000;

    public static int? ResolveDiscoveryTimeoutMilliseconds(ConfiguredMcpServerRecord server)
        => ResolveTimeoutMilliseconds(
            server.DiscoveryTimeoutMilliseconds ?? server.TimeoutMilliseconds,
            DefaultDiscoveryTimeoutMilliseconds,
            MaximumDiscoveryTimeoutMilliseconds);

    public static int? ResolveToolTimeoutMilliseconds(ConfiguredMcpServerRecord server)
        => ResolveTimeoutMilliseconds(
            server.ToolTimeoutMilliseconds ?? server.TimeoutMilliseconds,
            DefaultToolTimeoutMilliseconds,
            MaximumToolTimeoutMilliseconds);

    public static int? ResolveEffectiveTimeoutMilliseconds(int? serverTimeoutMilliseconds)
        => ResolveTimeoutMilliseconds(
            serverTimeoutMilliseconds,
            DefaultDiscoveryTimeoutMilliseconds,
            MaximumDiscoveryTimeoutMilliseconds);

    public static int? ResolveBackgroundRefreshTimeoutMilliseconds(int? discoveryTimeoutMilliseconds)
        => ResolveTimeoutMilliseconds(
            discoveryTimeoutMilliseconds,
            DefaultDiscoveryTimeoutMilliseconds,
            MaximumDiscoveryTimeoutMilliseconds);

    private static int ResolveTimeoutMilliseconds(int? timeoutMilliseconds, int fallback, int maximum)
        => timeoutMilliseconds is > 0
            ? Math.Min(timeoutMilliseconds.Value, maximum)
            : fallback;
}

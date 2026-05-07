namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpTimeoutResolver
{
    private const int DefaultDiscoveryTimeoutMilliseconds = 3_000;
    private const int MaxDiscoveryTimeoutMilliseconds = 5_000;
    private const int DefaultBackgroundRefreshTimeoutMilliseconds = 30_000;
    private const int DefaultEffectiveTimeoutMilliseconds = 300_000;

    public static int ResolveEffectiveTimeoutMilliseconds(int? serverTimeoutMilliseconds)
    {
        if (serverTimeoutMilliseconds is > 0)
        {
            return serverTimeoutMilliseconds.Value;
        }

        return DefaultEffectiveTimeoutMilliseconds;
    }

    public static int ResolveDiscoveryTimeoutMilliseconds(int? serverTimeoutMilliseconds)
        => serverTimeoutMilliseconds is > 0
            ? Math.Clamp(serverTimeoutMilliseconds.Value, 1, MaxDiscoveryTimeoutMilliseconds)
            : DefaultDiscoveryTimeoutMilliseconds;

    public static int ResolveBackgroundRefreshTimeoutMilliseconds(int? effectiveTimeoutMilliseconds)
        => effectiveTimeoutMilliseconds is > 0
            ? Math.Clamp(effectiveTimeoutMilliseconds.Value, 1, DefaultBackgroundRefreshTimeoutMilliseconds)
            : DefaultBackgroundRefreshTimeoutMilliseconds;
}

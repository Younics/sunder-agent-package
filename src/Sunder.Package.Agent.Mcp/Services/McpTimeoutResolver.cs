namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpTimeoutResolver
{
    internal const int DefaultDiscoveryTimeoutMilliseconds = 15_000;
    internal const int MaximumDiscoveryTimeoutMilliseconds = 120_000;
    internal const int MaximumToolTimeoutMilliseconds = 1_800_000;

    public static int? ResolveDiscoveryTimeoutMilliseconds(ConfiguredMcpServerRecord server)
        => ResolveTimeoutMilliseconds(
            ResolveConfiguredTimeout(server.DiscoveryTimeoutMilliseconds, server.TimeoutMilliseconds),
            DefaultDiscoveryTimeoutMilliseconds,
            MaximumDiscoveryTimeoutMilliseconds);

    public static int? ResolveToolTimeoutMilliseconds(ConfiguredMcpServerRecord server)
        => ResolveOptionalTimeoutMilliseconds(
            ResolveConfiguredTimeout(server.ToolTimeoutMilliseconds, server.TimeoutMilliseconds),
            MaximumToolTimeoutMilliseconds);

    public static McpToolExecutionTimeout? ResolveToolExecutionTimeout(
        int? serverTimeoutMilliseconds,
        int? agentTimeoutSeconds)
    {
        var serverTimeout = ResolveOptionalTimeoutMilliseconds(
            serverTimeoutMilliseconds,
            MaximumToolTimeoutMilliseconds);
        int? agentTimeout = agentTimeoutSeconds is > 0
            ? (int)Math.Min(
                (long)agentTimeoutSeconds.Value * 1000,
                MaximumToolTimeoutMilliseconds)
            : null;
        if (serverTimeout is null && agentTimeout is null)
        {
            return null;
        }

        return new McpToolExecutionTimeout(
            Math.Min(serverTimeout ?? int.MaxValue, agentTimeout ?? int.MaxValue),
            serverTimeout is not null && agentTimeout is not null
                ? McpToolTimeoutSource.ServerAndAgent
                : serverTimeout is not null
                    ? McpToolTimeoutSource.ServerConfiguration
                    : McpToolTimeoutSource.AgentRequest);
    }

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

    private static int? ResolveOptionalTimeoutMilliseconds(int? timeoutMilliseconds, int maximum)
        => timeoutMilliseconds is > 0
            ? Math.Min(timeoutMilliseconds.Value, maximum)
            : null;

    private static int? ResolveConfiguredTimeout(int? phaseTimeoutMilliseconds, int? legacyTimeoutMilliseconds)
        => phaseTimeoutMilliseconds is >= 0
            ? phaseTimeoutMilliseconds
            : legacyTimeoutMilliseconds;
}

internal readonly record struct McpToolExecutionTimeout(
    int Milliseconds,
    McpToolTimeoutSource Source);

internal enum McpToolTimeoutSource
{
    ServerConfiguration,
    AgentRequest,
    ServerAndAgent,
}

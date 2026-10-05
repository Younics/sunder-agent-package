namespace Sunder.Package.Agent.Mcp.Runtime;

internal sealed class McpRuntimeManagementClient(McpRuntimeHandler operations, McpRuntimeChangeStream changes) : IMcpOwnerManagementClient
{
    public async ValueTask<McpProjection> QueryAsync(McpQuery query, CancellationToken token = default)
        => await operations.HandleAsync(query, token).ConfigureAwait(false);
    public async ValueTask<McpProjection> CommandAsync(McpCommand command, CancellationToken token = default)
        => await operations.HandleAsync(command, token).ConfigureAwait(false);
    public IAsyncEnumerable<McpChanged> WatchChangesAsync(CancellationToken token = default) => changes.WatchAsync(token);
}

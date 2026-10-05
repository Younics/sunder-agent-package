using System.Runtime.CompilerServices;
using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal sealed class McpOwnerManagementRpcClient(ISunderRpcClient rpc) : IMcpOwnerManagementClient
{
    public ValueTask<McpProjection> QueryAsync(McpQuery query, CancellationToken token = default)
        => Invoke(query, "mcp-query.json", static (client, request, cancellation) => client.QueryAsync(request, cancellation), token);
    public ValueTask<McpProjection> CommandAsync(McpCommand command, CancellationToken token = default)
        => Invoke(command, "mcp-command.json", static (client, request, cancellation) => client.CommandAsync(request, cancellation), token);

    private async ValueTask<McpProjection> Invoke<T>(T value, string fileName,
        Func<Wire.ManagementClient, Wire.ManagementContent, CancellationToken, ValueTask<Wire.ManagementContent>> invoke, CancellationToken token) where T : notnull
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(api, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var client = api.CreateClient(scope, provider);
        var content = await McpManagementContent.RegisterAsync(value, fileName, scope.DeadlineUtc, client.RegisterContentAsync, token).ConfigureAwait(false);
        var response = await invoke(client.SManagement, new() { Content = content }, token).ConfigureAwait(false);
        return await McpManagementContent.ReadResponseAsync<McpProjection>(scope, response.Content, token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<McpChanged> WatchChangesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(api, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var client = api.CreateClient(scope, provider).SManagement;
        await foreach (var change in client.ChangesAsync(new Wire.ChangeSubscription(), token).WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return new(change.Kind switch
            {
                Wire.ManagementChangeKind.Servers => McpChangeKind.Servers,
                Wire.ManagementChangeKind.Status => McpChangeKind.Status,
                _ => throw new InvalidDataException("The MCP Runtime returned an unknown change kind."),
            });
        }
    }

    private static async ValueTask<Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Mcp_Management.Provider> Resolve(
        Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Mcp_Management.Contract api, CancellationToken token)
    {
        try { return await api.ResolveSelectedPackageAsync(token).ConfigureAwait(false); }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found")
        { throw new InvalidOperationException("The MCP Runtime management provider is unavailable.", error); }
    }
}

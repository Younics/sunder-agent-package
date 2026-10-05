using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.mcp.management", ServiceId = "management", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "mcp.management")]
[SunderRpcDomainError("mcp.management.failed", Description = "The MCP management request failed.")]
internal sealed partial class ManagementService(IMcpOwnerManagementClient operations)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["mcp.management.failed"])]
    public async ValueTask<ManagementContent> QueryAsync(ManagementContent request, SunderRpcInvocationContext context, CancellationToken token = default)
    {
        try
        {
            var query = await McpManagementContent.ReadRequestAsync<McpQuery>(context, request.Content.Value, token).ConfigureAwait(false);
            var result = await operations.QueryAsync(query, token).ConfigureAwait(false);
            return new() { Content = new() { Value = await McpManagementContent.RegisterResponseAsync(context, result, "mcp-projection.json", token).ConfigureAwait(false) } };
        }
        catch (McpManagementOperationException error) { throw Failure(error); }
    }

    [SunderRpcMethod(Errors = ["mcp.management.failed"])]
    public async ValueTask<ManagementContent> CommandAsync(ManagementContent request, SunderRpcInvocationContext context, CancellationToken token = default)
    {
        try
        {
            var command = await McpManagementContent.ReadRequestAsync<McpCommand>(context, request.Content.Value, token).ConfigureAwait(false);
            var result = await operations.CommandAsync(command, token).ConfigureAwait(false);
            return new() { Content = new() { Value = await McpManagementContent.RegisterResponseAsync(context, result, "mcp-projection.json", token).ConfigureAwait(false) } };
        }
        catch (McpManagementOperationException error) { throw Failure(error); }
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async IAsyncEnumerable<ManagementChange> ChangesAsync(ChangeSubscription request, [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        yield return new() { Kind = ManagementChangeKind.Servers };
        await foreach (var change in operations.WatchChangesAsync(token).WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return new() { Kind = change.Kind == McpChangeKind.Servers ? ManagementChangeKind.Servers : ManagementChangeKind.Status };
        }
    }

    private static SunderRpcException Failure(McpManagementOperationException error)
        => new(new(SunderRpcErrorKind.Domain, "mcp.management.failed", error.Message));
}

[SunderRpcTransparent]
internal sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }
internal sealed class ManagementContent { public required ContentReference Content { get; init; } }
internal sealed class ChangeSubscription;
internal enum ManagementChangeKind
{
    [JsonStringEnumMemberName("servers")] Servers,
    [JsonStringEnumMemberName("status")] Status,
}
internal sealed class ManagementChange { public required ManagementChangeKind Kind { get; init; } }

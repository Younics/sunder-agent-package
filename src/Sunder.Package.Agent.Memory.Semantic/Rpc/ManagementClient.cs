using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using InspectorApi = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Memory_Semantic_Inspector;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

internal sealed class MemoryInspectorSourceClient(ISunderRpcClient rpc)
{
    private static readonly JsonSerializerOptions Json = MemoryValueJson.CreateOptions();

    public ValueTask<MemoryProjection> QueryAsync(MemoryQuery request, CancellationToken token = default)
        => Invoke(MemoryValueJson.Convert<Wire.MemoryQuery>(request, Json), static (client, value, cancellation) => client.QueryAsync(value, cancellation), token);

    public ValueTask<MemoryProjection> CommandAsync(MemoryCommand request, CancellationToken token = default)
        => Invoke(MemoryValueJson.Convert<Wire.MemoryCommand>(request, Json), static (client, value, cancellation) => client.CommandAsync(value, cancellation), token);

    private async ValueTask<MemoryProjection> Invoke<T>(T request,
        Func<Wire.MemoryInspectorClient, T, CancellationToken, ValueTask<Wire.MemoryProjectionContent>> invoke, CancellationToken token)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await ResolveInspectorAsync(api, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var result = await invoke(api.CreateClient(scope, provider).SMemory_2dinspector, request, token).ConfigureAwait(false);
        return await ProjectionContent.Read(scope, result.Content, token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<MemoryChanged> ChangesAsync(MemoryChangeSubscription request, [EnumeratorCancellation] CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await ResolveInspectorAsync(api, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        await foreach (var value in api.CreateClient(scope, provider).SMemory_2dinspector.ChangesAsync(new Wire.MemoryChangeSubscription(), token).WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return MemoryValueJson.Convert<MemoryChanged>(value, Json);
        }
    }

    private static async ValueTask<InspectorApi.Provider> ResolveInspectorAsync(InspectorApi.Contract api, CancellationToken token)
    {
        // Discovery retains generated descriptor/ABI validation. Other providers
        // of this contract must not change which owner endpoint this gateway uses.
        var catalog = await api.ListAsync(token).ConfigureAwait(false);
        return catalog.Providers.SingleOrDefault(provider =>
            provider.PackageId == "sunder.package.agent.memory.semantic"
            && provider.ProviderId == "semantic.memory.inspector"
            && provider.State == SunderRpcProviderState.Active)
            ?? throw new SunderRpcException(new(SunderRpcErrorKind.NotFound, "rpc.provider-not-found",
                "The semantic memory inspector Runtime provider is unavailable."));
    }
}

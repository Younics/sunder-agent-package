using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed partial class MemoryAppRuntimeGateway
{
    private ValueTask<MemoryProjection> QueryTransportAsync(MemoryQuery request, CancellationToken token)
        => InvokeTransportAsync(client => client.QueryAsync(request, token));
    private ValueTask<MemoryProjection> CommandTransportAsync(MemoryCommand request, CancellationToken token)
        => InvokeTransportAsync(client => client.CommandAsync(request, token));

    private async ValueTask<MemoryProjection> InvokeTransportAsync(Func<MemoryInspectorSourceClient, ValueTask<MemoryProjection>> invoke)
    {
        try { return await invoke(new MemoryInspectorSourceClient(_rpc)).ConfigureAwait(false); }
        catch (SunderRpcException error) when (IsMissingProvider(error)) { throw Unavailable(); }
    }

    private async IAsyncEnumerable<MemoryChanged> WatchTransportAsync([EnumeratorCancellation] CancellationToken token)
    {
        await using var changes = new MemoryInspectorSourceClient(_rpc).ChangesAsync(new MemoryChangeSubscription(), token).GetAsyncEnumerator(token);
        while (await MoveNextAsync(changes).ConfigureAwait(false)) yield return changes.Current;
    }

    private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<MemoryChanged> changes)
    {
        try { return await changes.MoveNextAsync().ConfigureAwait(false); }
        catch (SunderRpcException error) when (IsMissingProvider(error)) { throw Unavailable(); }
    }

    private static bool IsMissingProvider(SunderRpcException error)
        => error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found";
    private static SunderRpcException Unavailable()
        => new(new(SunderRpcErrorKind.Unavailable, "semantic-memory.inspector-unavailable", "The semantic memory inspector Runtime provider is unavailable."));
}

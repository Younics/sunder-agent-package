using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class CatalogClientInvocation<T>(ISunderRpcClient rpc, Func<ISunderRpcCallScope, T> create,
    Func<CancellationToken, ValueTask<bool>> current, CancellationToken retirement)
{
    internal TResult Invoke<TResult>(Func<T, CancellationToken, ValueTask<TResult>> callback)
        => InvokeAsync(callback, default).AsTask().GetAwaiter().GetResult();
    internal async ValueTask<TResult> InvokeAsync<TResult>(Func<T, CancellationToken, ValueTask<TResult>> callback, CancellationToken token)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, retirement);
        invocation.Token.ThrowIfCancellationRequested();
        await RequireCurrent(invocation.Token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: invocation.Token).ConfigureAwait(false);
        var result = await callback(create(scope), invocation.Token).ConfigureAwait(false);
        invocation.Token.ThrowIfCancellationRequested();
        await RequireCurrent(invocation.Token).ConfigureAwait(false);
        return result;
    }
    internal async ValueTask RequireCurrent(CancellationToken token)
    {
        if (!await current(token).ConfigureAwait(false))
            throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "subagents.catalog.stale", "The catalog provider activation is stale."));
    }
}

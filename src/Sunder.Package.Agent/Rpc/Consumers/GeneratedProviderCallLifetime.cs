using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime.Rpc;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class GeneratedProviderCallLifetime(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider, string staleCode,
    string staleMessage = "The selected provider activation is no longer current.") : IDisposable, IAsyncDisposable
{
    private readonly AgentRpcProviderHandle _identity = AgentRpcProviderHandle.From(provider);
    private readonly CancellationTokenSource _retirement = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task? _stop;
    private int _active;
    private bool _stopped;

    internal void ThrowIfStopped()
    {
        lock (_gate) if (_stopped) throw Stale();
    }

    internal async ValueTask<T> Invoke<T>(Func<ISunderRpcCallScope, CancellationToken, ValueTask<T>> invoke, CancellationToken token, SunderRpcCallOptions? options = null)
    {
        var retirement = Enter(token);
        try
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, retirement);
            await using var scope = await rpc.CreateCallScopeAsync(options, invocation.Token).ConfigureAwait(false);
            await EnsureCurrent(scope, invocation.Token).ConfigureAwait(false);
            var result = await invoke(scope, invocation.Token).ConfigureAwait(false);
            await EnsureCurrent(scope, invocation.Token).ConfigureAwait(false);
            invocation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally { Exit(); }
    }

    internal async IAsyncEnumerable<T> Subscribe<T>(Func<ISunderRpcCallScope, CancellationToken, IAsyncEnumerable<T>> subscribe,
        [EnumeratorCancellation] CancellationToken token = default, SunderRpcCallOptions? options = null)
    {
        var retirement = Enter(token);
        try
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, retirement);
            await using var scope = await rpc.CreateCallScopeAsync(options, invocation.Token).ConfigureAwait(false);
            await EnsureCurrent(scope, invocation.Token).ConfigureAwait(false);
            await foreach (var value in subscribe(scope, invocation.Token).WithCancellation(invocation.Token).ConfigureAwait(false))
            {
                await EnsureCurrent(scope, invocation.Token).ConfigureAwait(false);
                invocation.Token.ThrowIfCancellationRequested();
                yield return value;
            }
            await EnsureCurrent(scope, invocation.Token).ConfigureAwait(false);
        }
        finally { Exit(); }
    }

    private CancellationToken Enter(CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_stopped) throw Stale();
            _active++;
            return _retirement.Token;
        }
    }

    private void Exit()
    {
        lock (_gate) if (--_active == 0 && _stopped) _drained.TrySetResult();
    }

    private async ValueTask EnsureCurrent(ISunderRpcCallScope scope, CancellationToken token)
    {
        var current = await scope.GetProviderAsync(provider.Endpoint, token).ConfigureAwait(false);
        if (current is null || current.State != SunderRpcProviderState.Active || !_identity.Matches(current)) throw Stale();
    }

    private SunderRpcException Stale() => new(new(SunderRpcErrorKind.StaleEndpoint, staleCode, staleMessage));

    public void Dispose() => _ = DisposeAsync();
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_stop is not null) return new(_stop);
            _stopped = true;
            if (_active == 0) _drained.TrySetResult();
            return new(_stop = StopAsync());
        }
    }
    private async Task StopAsync()
    {
        try { await _retirement.CancelAsync().ConfigureAwait(false); }
        finally { await _drained.Task.ConfigureAwait(false); _retirement.Dispose(); }
    }
}

using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Runtime_Catalog;

namespace Sunder.Package.Agent.Mcp.Rpc;

public sealed class RuntimeWorkspaceCatalog(ISunderRpcClient rpc) : IMcpWorkspaceCatalog
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stop;
    private bool _started;
    private bool _stopped;
    private int _calls;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopped, this); _started = true; }
        return ValueTask.CompletedTask;
    }

    public async Task<IReadOnlyList<AgentWorkspaceRecord>> ListKnownWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_started) return [];
            _calls++;
        }
        try
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = invocation.Token;
            await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
            var contract = new AgentPackageApi(scope).CSunder_Agent_Runtime_Catalog;
            var snapshot = await contract.ListAsync(token).ConfigureAwait(false);
            var result = new List<AgentWorkspaceRecord>();
            foreach (var provider in snapshot.Providers)
            {
                token.ThrowIfCancellationRequested();
                if (provider.State != SunderRpcProviderState.Active || !await IsCurrent(contract, provider, token).ConfigureAwait(false)) continue;
                IReadOnlyList<AgentWorkspaceRecord> workspaces;
                try
                {
                    workspaces = McpProviderValues.Convert<AgentWorkspaceRecord[]>(await contract.CreateClient(scope, provider)
                        .SRuntime_2dcatalog.ListWorkspacesAsync(new(), token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    // Workspace configuration synchronization is best-effort per
                    // optional provider. Discovery/selection failures stay separate.
                    token.ThrowIfCancellationRequested();
                    continue;
                }
                token.ThrowIfCancellationRequested();
                if (await IsCurrent(contract, provider, token).ConfigureAwait(false)) result.AddRange(workspaces);
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (SunderRpcException error) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested
            && error.Error.Kind == SunderRpcErrorKind.Unavailable) { return []; }
        finally { lock (_gate) { if (--_calls == 0 && _stopped) _drained.TrySetResult(); } }
    }

    private static async ValueTask<bool> IsCurrent(Api.Contract contract, Api.Provider provider, CancellationToken token)
    {
        try
        {
            var current = await contract.GetAsync(provider, token).ConfigureAwait(false);
            return current is { State: SunderRpcProviderState.Active } && current.SessionGeneration == provider.SessionGeneration;
        }
        catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        { token.ThrowIfCancellationRequested(); return false; }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stop is not null) return new(_stop);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task; _stopped = true;
            if (_calls == 0) _drained.TrySetResult();
        }
        _ = StopAsync(completion);
        return new(completion.Task);
    }
    private async Task StopAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await _drained.Task.ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally { _lifetime.Dispose(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
}

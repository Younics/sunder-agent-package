using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Execution_Target;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

internal sealed class FilesExecutionBindings(ISunderRpcClient rpc, FilesToolSource source) : IFilesRuntimeBindings, ISunderWorkerActivationService
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<TargetLease> _leases = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _watch = Task.CompletedTask;
    private Task? _stop;
    private bool _started;
    private bool _stopped;
    private int _operations;

    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Activate();
        return StartAsync(cancellationToken);
    }

    public ValueTask StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_started) { _started = true; _watch = Task.Run(WatchAsync); }
        }
        return ValueTask.CompletedTask;
    }

    internal FileExecutionReference? Select(AgentRpcProviderHandle? selected, DateTimeOffset deadline)
        => selected is null ? null : new(token => AcquireAsync(selected, deadline, token));

    private async ValueTask<FileExecutionLease?> AcquireAsync(AgentRpcProviderHandle handle, DateTimeOffset deadline, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_started || _stopped) return null;
            _operations++;
        }
        ISunderRpcCallScope? scope = null;
        TargetLease? lease = null;
        var transferred = false;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
            var maximum = DateTimeOffset.UtcNow.AddMinutes(35);
            scope = await rpc.CreateCallScopeAsync(new SunderRpcCallOptions(DeadlineUtc: deadline < maximum ? deadline : maximum), opening.Token).ConfigureAwait(false);
            var snapshot = await scope.GetProviderAsync(new(handle.EndpointReference), opening.Token).ConfigureAwait(false);
            if (!Matches(handle, snapshot)) return null;
            var api = new AgentPackageApi(scope).CSunder_Agent_Execution_Target;
            var provider = new Api.Provider(snapshot!);
            if (!Matches(handle, (await api.GetAsync(provider, opening.Token).ConfigureAwait(false))?.Snapshot)) return null;
            lease = new TargetLease(this, handle, scope, api, provider, _lifetime.Token);
            lock (_gate) { if (_stopped) return null; _leases.Add(lease); }
            var description = await lease.InvokeAsync(static (client, token) => client.DescribeAsync(new(), token), caller).ConfigureAwait(false);
            var target = new ExecutionClient(lease, FilesProviderValues.Convert<Sunder.Package.Agent.Contracts.Models.AgentExecutionTargetDescriptor>(description));
            var result = new FileExecutionLease(target, lease.RetirementToken, _ => ValueTask.FromResult(target.Descriptor), lease.IsCurrentAsync, lease.DisposeAsync);
            transferred = true;
            return result;
        }
        catch (Exception error) when (!caller.IsCancellationRequested && (error is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable }
            || (_lifetime.IsCancellationRequested || lease?.RetirementToken.IsCancellationRequested == true)
               && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled })) { return null; }
        finally
        {
            if (!transferred)
            {
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
                else
                {
                    try { if (scope is not null) await scope.DisposeAsync().ConfigureAwait(false); }
                    finally { Release(null); }
                }
            }
        }
    }

    private async Task WatchAsync()
    {
        var api = new AgentPackageApi(rpc).CSunder_Agent_Execution_Target;
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in api.WatchAsync(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    TargetLease[] retired;
                    lock (_gate) retired = _leases.Where(lease => change.Kind == SunderRpcCatalogEventKind.ResetRequired
                        || change.Provider is { } provider && provider.Snapshot.Endpoint.Value == lease.Handle.EndpointReference
                            && (change.Kind is SunderRpcCatalogEventKind.Deactivated or SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Faulted
                                || !Matches(lease.Handle, provider.Snapshot))).ToArray();
                    foreach (var lease in retired) lease.Retire();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { }
            TargetLease[] invalidated;
            lock (_gate) invalidated = _leases.ToArray();
            foreach (var lease in invalidated) lease.Retire();
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }

    private void Release(TargetLease? lease)
    {
        lock (_gate)
        {
            if (lease is not null) _leases.Remove(lease);
            if (--_operations == 0 && _stopped) _drained.TrySetResult();
        }
    }

    private static bool Matches(AgentRpcProviderHandle handle, SunderRpcProviderSnapshot? value)
        => value is not null && value.State == SunderRpcProviderState.Active && handle.EndpointReference == value.Endpoint.Value
           && handle.PackageId == value.PackageId && handle.PackageVersion == value.PackageVersion && handle.ProviderId == value.ProviderId
           && handle.ContractId == value.ContractId && handle.ContractVersion == value.ContractVersion
           && handle.DescriptorSha256 == value.DescriptorSha256 && handle.AbiSha256 == value.AbiSha256
           && handle.ActivationId == value.ActivationId && handle.ActivationEpoch == value.ActivationEpoch && handle.SessionGeneration == value.SessionGeneration;

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stop is not null) return new(_stop);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = completion.Task; _stopped = true;
            if (_operations == 0) _drained.TrySetResult();
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
            finally { await Task.WhenAll(_watch, _drained.Task).ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally { _lifetime.Dispose(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }

    internal sealed class TargetLease : IAsyncDisposable
    {
        private readonly FilesExecutionBindings _owner;
        private readonly ISunderRpcCallScope _scope;
        private readonly Api.Contract _api;
        private readonly Api.Provider _provider;
        private readonly Wire.ExecutionTargetClient _client;
        private readonly CancellationTokenSource _retirement;
        private int _disposed;
        internal TargetLease(FilesExecutionBindings owner, AgentRpcProviderHandle handle, ISunderRpcCallScope scope, Api.Contract api, Api.Provider provider, CancellationToken lifetime)
        {
            _owner = owner; Handle = handle; _scope = scope; _api = api; _provider = provider;
            _client = api.CreateClient(scope, provider).SExecution_2dtarget;
            _retirement = CancellationTokenSource.CreateLinkedTokenSource(lifetime); RetirementToken = _retirement.Token;
        }
        internal AgentRpcProviderHandle Handle { get; }
        internal CancellationToken RetirementToken { get; }
        internal void Retire()
        {
            try { _retirement.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { /* Every linked operation must observe retirement. */ }
        }
        internal async ValueTask<bool> IsCurrentAsync(CancellationToken token)
        {
            if (RetirementToken.IsCancellationRequested) return false;
            var current = await _api.GetAsync(_provider, token).ConfigureAwait(false);
            if (Matches(Handle, current?.Snapshot)) return true;
            Retire(); return false;
        }
        internal async ValueTask<T> InvokeAsync<T>(Func<Wire.ExecutionTargetClient, CancellationToken, ValueTask<T>> invoke, CancellationToken token)
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, RetirementToken);
            invocation.Token.ThrowIfCancellationRequested();
            if (!await IsCurrentAsync(invocation.Token).ConfigureAwait(false)) throw Stale();
            var result = await invoke(_client, invocation.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!await IsCurrentAsync(invocation.Token).ConfigureAwait(false)) throw Stale();
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await _scope.DisposeAsync().ConfigureAwait(false); }
            finally { _retirement.Dispose(); _owner.Release(this); }
        }
        private static SunderRpcException Stale() => new(new(SunderRpcErrorKind.StaleEndpoint, "files.execution.stale", "The selected execution target is no longer current."));
    }
}

using System.Runtime.CompilerServices;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Sdk.Rpc;
using CatalogApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Runtime_Catalog;
using ExecutionApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Execution_Target;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Skills.Rpc;

public sealed class SkillsRuntimeBindings : ISkillsRuntimeBindings
{
    private readonly ISunderRpcClient _rpc;
    private readonly CatalogApi.Contract _catalog;
    private readonly ExecutionApi.Contract _execution;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Target> _targets = new(StringComparer.Ordinal);
    private readonly List<Target> _retired = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _watch = Task.CompletedTask;
    private Task? _stop;
    private bool _started;
    private bool _stopped;
    private int _calls;

    public SkillsRuntimeBindings(ISunderRpcClient rpc)
    {
        _rpc = rpc;
        var api = new AgentPackageApi(rpc);
        _catalog = api.CSunder_Agent_Runtime_Catalog;
        _execution = api.CSunder_Agent_Execution_Target;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_started)
            {
                _started = true;
                _watch = Task.WhenAll(Task.Run(() => WatchAsync(catalog: true)), Task.Run(() => WatchAsync(catalog: false)));
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<AgentProfileRecord?> GetProfileAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profileId) || !Enter()) return null;
        Target? target = null;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var provider = await _catalog.ResolveSelectedPackageAsync(lifetime.Token).ConfigureAwait(false);
            var snapshot = provider.Snapshot;
            target = Track(new(snapshot.PackageId, snapshot.PackageVersion, snapshot.ProviderId,
                snapshot.ContractId, snapshot.ContractVersion, snapshot.DescriptorSha256, snapshot.AbiSha256,
                snapshot.ActivationId, snapshot.ActivationEpoch, snapshot.SessionGeneration, snapshot.Endpoint.Value));
            if (target is null) return null;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, target.Retirement.Token);
            if (!Matches(target.Handle, (await _catalog.GetAsync(provider, invocation.Token).ConfigureAwait(false))?.Snapshot)) return null;
            await using var scope = await _rpc.CreateCallScopeAsync(cancellationToken: invocation.Token).ConfigureAwait(false);
            var value = await _catalog.CreateClient(scope, provider).SRuntime_2dcatalog.GetProfileAsync(new() { Value = profileId }, invocation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Retirement.IsCancellationRequested || !Matches(target.Handle,
                    (await _catalog.GetAsync(provider, lifetime.Token).ConfigureAwait(false))?.Snapshot)) return null;
            return SkillsProviderValues.Optional<AgentProfileRecord>(value);
        }
        catch (SunderRpcException error) when (!cancellationToken.IsCancellationRequested
            && error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found") { return null; }
        catch (Exception error) when (IsUnavailable(error, target, cancellationToken)) { return null; }
        finally { Exit(); }
    }

    public async ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentExecutionTargetContext context, IReadOnlyList<AgentExecutionResourceDescriptor> resources,
        AgentRpcProviderHandle? selectedTarget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedTarget is null || !Enter()) return [];
        Target? target = null;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var snapshot = await _rpc.GetProviderAsync(new(selectedTarget.EndpointReference), lifetime.Token).ConfigureAwait(false);
            if (!Matches(selectedTarget, snapshot)) return [];
            target = Track(selectedTarget);
            if (target is null) return [];
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, target.Retirement.Token);
            var provider = new ExecutionApi.Provider(snapshot!);
            if (!Matches(selectedTarget, (await _execution.GetAsync(provider, invocation.Token).ConfigureAwait(false))?.Snapshot)) return [];
            await using var scope = await _rpc.CreateCallScopeAsync(cancellationToken: invocation.Token).ConfigureAwait(false);
            var client = _execution.CreateClient(scope, provider).SExecution_2dtarget;
            var descriptor = await client.DescribeAsync(new(), invocation.Token).ConfigureAwait(false);
            if ((!string.Equals(descriptor.TargetId, context.Binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(descriptor.TargetKind, context.Binding.ContributionId, StringComparison.OrdinalIgnoreCase))
                || !descriptor.Facets.Contains("resource-resolution", StringComparer.Ordinal)) return [];
            var result = await client.ResolveResourcesAsync(new Wire.ExecutionResourcesRequest
            {
                Context = new()
                {
                    Context = SkillsProviderValues.Convert<Wire.AgentExecutionTargetContext>(context),
                    ApprovedResourceCapabilities = [], CapturePostMutationResource = false,
                },
                Resources = SkillsProviderValues.Convert<Wire.AgentExecutionResourceDescriptor[]>(resources),
            }, invocation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Retirement.IsCancellationRequested || !Matches(selectedTarget,
                    await _rpc.GetProviderAsync(new(selectedTarget.EndpointReference), lifetime.Token).ConfigureAwait(false))) return [];
            return SkillsProviderValues.Convert<AgentResolvedExecutionResource[]>(result);
        }
        catch (Exception error) when (IsUnavailable(error, target, cancellationToken)) { return []; }
        finally { Exit(); }
    }

    private bool IsUnavailable(Exception error, Target? target, CancellationToken caller)
        => !caller.IsCancellationRequested
            && (error is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable }
                || (target?.Retirement.IsCancellationRequested == true || _lifetime.IsCancellationRequested)
                   && (error is OperationCanceledException || error is SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled }));

    private Target? Track(AgentRpcProviderHandle handle)
    {
        lock (_gate)
        {
            if (_stopped) return null;
            if (!_targets.TryGetValue(handle.EndpointReference, out var target) || target.Handle != handle)
            {
                if (target is not null) Retire(target);
                target = new(handle);
                _targets[handle.EndpointReference] = target;
            }
            return target;
        }
    }
    private bool Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_started) return false;
            _calls++;
            return true;
        }
    }
    private void Exit()
    {
        lock (_gate)
        {
            if (--_calls != 0) return;
            DisposeRetired();
            if (_stopped) _drained.TrySetResult();
        }
    }
    private async IAsyncEnumerable<Change> Changes(bool catalog, long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        if (catalog)
        {
            await foreach (var change in _catalog.WatchAsync(revision, sequence, token).ConfigureAwait(false))
                yield return new(change.Revision, change.Sequence, change.Kind, change.Provider?.Snapshot);
        }
        else
        {
            await foreach (var change in _execution.WatchAsync(revision, sequence, token).ConfigureAwait(false))
                yield return new(change.Revision, change.Sequence, change.Kind, change.Provider?.Snapshot);
        }
    }
    private async Task WatchAsync(bool catalog)
    {
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in Changes(catalog, revision, sequence, _lifetime.Token).ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    lock (_gate)
                    {
                        if (change.Kind == SunderRpcCatalogEventKind.ResetRequired) RetireAll();
                        else if (change.Provider is { } provider && _targets.TryGetValue(provider.Endpoint.Value, out var target)
                            && (change.Kind is SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Deactivated or SunderRpcCatalogEventKind.Faulted
                                || !Matches(target.Handle, provider)))
                        {
                            _targets.Remove(provider.Endpoint.Value);
                            Retire(target);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is OperationCanceledException or SunderRpcException)
            { lock (_gate) RetireAll(); }
            lock (_gate) RetireAll();
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }
    private void RetireAll()
    {
        foreach (var target in _targets.Values) Retire(target);
        _targets.Clear();
    }
    private void Retire(Target target)
    {
        _retired.Add(target);
        try { target.Retirement.Cancel(); }
        catch (AggregateException) { /* Retirement must reach all linked operations. */ }
        if (_calls == 0) DisposeRetired();
    }
    private void DisposeRetired()
    {
        foreach (var target in _retired) target.Retirement.Dispose();
        _retired.Clear();
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
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task;
            _stopped = true;
            RetireAll();
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
            finally { await Task.WhenAll(_watch, _drained.Task).ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_gate) DisposeRetired();
            _lifetime.Dispose();
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    private sealed record Target(AgentRpcProviderHandle Handle)
    {
        internal CancellationTokenSource Retirement { get; } = new();
    }
    private sealed record Change(long Revision, long Sequence, SunderRpcCatalogEventKind Kind, SunderRpcProviderSnapshot? Provider);
}

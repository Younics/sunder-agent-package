using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;
using CatalogApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Runtime_Catalog;
using ChildApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Child_Run_Executor;

namespace Sunder.Package.Agent.Subagents.Rpc;

public sealed partial class SubagentRuntimeBindings : ISubagentRuntimeBindings
{
    private readonly ISunderRpcClient _rpc;
    private readonly AgentPackageApi _api;
    private readonly CatalogApi.Contract _catalog;
    private readonly ChildApi.Contract _children;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Reference<IAgentRuntimeCatalog>> _catalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reference<IAgentChildRunExecutor>> _executors = new(StringComparer.Ordinal);
    private readonly List<Task> _retirements = [];
    private Task _watch = Task.CompletedTask;
    private Task? _stop;
    private bool _started;
    private bool _stopped;
    public SubagentRuntimeBindings(ISunderRpcClient rpc)
    {
        _rpc = rpc;
        _api = new AgentPackageApi(rpc);
        _catalog = _api.CSunder_Agent_Runtime_Catalog;
        _children = _api.CSunder_Agent_Child_Run_Executor;
    }
    public event Action? RuntimeCatalogChanged;
    public event Action<SubagentCatalogChange>? CatalogChanged;
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) return ValueTask.CompletedTask;
            _started = true;
            _watch = Task.WhenAll(AdditionalWatches().Concat([
                Task.Run(() => WatchAsync(CatalogChanges, _catalogs, SubagentCatalogChange.RuntimeCatalog)),
                Task.Run(() => WatchAsync(ChildChanges, _executors, SubagentCatalogChange.None))]));
        }
        return ValueTask.CompletedTask;
    }
    public IReadOnlyList<SubagentProviderReference<IAgentRuntimeCatalog>> GetRuntimeCatalogs()
    {
        if (!IsStarted()) return [];
        try
        {
            var snapshot = _catalog.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_catalogs, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => _catalog.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new RuntimeCatalogClient(_rpc, _catalog, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_catalogs); return []; }
    }
    public IReadOnlyList<SubagentProviderReference<IAgentChildRunExecutor>> GetChildRunExecutors()
    {
        if (!IsStarted()) return [];
        try
        {
            var snapshot = _children.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_executors, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => _children.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new ChildRunClient(_rpc, _children, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_executors); return []; }
    }
    private bool IsStarted() { lock (_gate) { ObjectDisposedException.ThrowIf(_stopped, this); return _started; } }

    private IReadOnlyList<SubagentProviderReference<T>> Reconcile<T, TProvider>(Dictionary<string, Reference<T>> known,
        (SunderRpcProviderSnapshot Snapshot, TProvider Value)[] providers, bool reset, Func<TProvider, Reference<T>> create) where T : class
    {
        var retired = new List<Reference<T>>();
        var result = new List<SubagentProviderReference<T>>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var active = providers.Where(value => value.Snapshot.State == SunderRpcProviderState.Active).ToArray();
            var endpoints = active.Select(value => value.Snapshot.Endpoint.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in known.Where(pair => reset || !endpoints.Contains(pair.Key)).ToArray())
            { known.Remove(pair.Key); QueueRetirement(pair.Value, retired); }
            foreach (var provider in active)
            {
                var key = provider.Snapshot.Endpoint.Value;
                if (!known.TryGetValue(key, out var reference) || !reference.CanReuse(provider.Snapshot))
                {
                    if (reference is not null) QueueRetirement(reference, retired);
                    reference = create(provider.Value);
                    known[key] = reference;
                }
                result.Add(reference);
            }
        }
        foreach (var reference in retired) reference.Retire();
        return result;
    }
    private void QueueRetirement<T>(Reference<T> reference, List<Reference<T>> retired) where T : class
    {
        _retirements.RemoveAll(task => task.IsCompletedSuccessfully);
        _retirements.Add(reference.Completion);
        retired.Add(reference);
    }
    private void Invalidate<T>(Dictionary<string, Reference<T>> known) where T : class
    {
        var retired = new List<Reference<T>>();
        lock (_gate)
        {
            foreach (var reference in known.Values) QueueRetirement(reference, retired);
            known.Clear();
        }
        foreach (var reference in retired) reference.Retire();
    }
    private async IAsyncEnumerable<Change> CatalogChanges(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var change in _catalog.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(change.Revision, change.Sequence, change.Kind, change.Provider?.Snapshot);
    }
    private async IAsyncEnumerable<Change> ChildChanges(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var change in _children.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(change.Revision, change.Sequence, change.Kind, change.Provider?.Snapshot);
    }
    private async Task WatchAsync<T>(Func<long, long, CancellationToken, IAsyncEnumerable<Change>> watch, Dictionary<string, Reference<T>> known, SubagentCatalogChange changeKind) where T : class
    {
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in watch(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    if (change.Kind == SunderRpcCatalogEventKind.ResetRequired) Invalidate(known);
                    else if (change.Provider is { } provider)
                    {
                        var retired = new List<Reference<T>>();
                        lock (_gate)
                        {
                            if (known.TryGetValue(provider.Endpoint.Value, out var reference)
                                && (change.Kind is SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Deactivated or SunderRpcCatalogEventKind.Faulted
                                    || !reference.CanReuse(provider)))
                            { known.Remove(provider.Endpoint.Value); QueueRetirement(reference, retired); }
                        }
                        foreach (var reference in retired) reference.Retire();
                    }
                    Notify(changeKind);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is OperationCanceledException or SunderRpcException) { /* Reconcile after metadata transport loss. */ }
            Invalidate(known);
            Notify(changeKind);
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }
    private void Notify(SubagentCatalogChange change)
    {
        if ((change & SubagentCatalogChange.RuntimeCatalog) != 0 && RuntimeCatalogChanged is { } runtimeHandlers)
            foreach (Action handler in runtimeHandlers.GetInvocationList())
            { try { handler(); } catch { /* One observer cannot prevent retirement and refresh of the others. */ } }
        if (change != SubagentCatalogChange.None && CatalogChanged is { } handlers)
            foreach (Action<SubagentCatalogChange> handler in handlers.GetInvocationList())
            { try { handler(change); } catch { /* Independent catalog observers own their failures. */ } }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        IRetirable[] references;
        Task[] work;
        lock (_gate)
        {
            if (_stop is not null) return new(_stop);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task; _stopped = true;
            references = _catalogs.Values.Cast<IRetirable>().Concat(_executors.Values).Concat(AdditionalReferences()).ToArray();
            _catalogs.Clear(); _executors.Clear();
            ClearAdditionalReferences();
            work = _retirements.Concat(references.Select(value => value.Completion)).Append(_watch).ToArray();
        }
        foreach (var reference in references) reference.Retire();
        _ = StopAsync(work, completion);
        return new(completion.Task);
    }
    private async Task StopAsync(Task[] work, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(work).ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally { _lifetime.Dispose(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    private sealed record Change(long Revision, long Sequence, SunderRpcCatalogEventKind Kind, SunderRpcProviderSnapshot? Provider);
    private interface IRetirable { Task Completion { get; } void Retire(); }
    private sealed class Reference<T> : SubagentProviderReference<T>, IRetirable where T : class
    {
        private readonly Func<CancellationToken, SunderRpcProviderSnapshot?> _get;
        private readonly CancellationTokenSource _retirement = new();
        private readonly CancellationToken _token;
        private readonly T _service;
        private readonly object _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _retired;
        private int _leases;
        public override SunderRpcProviderSnapshot Provider { get; }
        public Task Completion => _cleanup.Task;
        internal Reference(SunderRpcProviderSnapshot provider, Func<CancellationToken, SunderRpcProviderSnapshot?> get, Func<CancellationToken, T> create)
        { Provider = provider; _get = get; _token = _retirement.Token; _service = create(_token); }
        internal bool CanReuse(SunderRpcProviderSnapshot value)
            => !Volatile.Read(ref _retired) && value.Endpoint == Provider.Endpoint && value.PackageId == Provider.PackageId && value.PackageVersion == Provider.PackageVersion
                && value.ProviderId == Provider.ProviderId && value.ContractId == Provider.ContractId && value.ContractVersion == Provider.ContractVersion
                && value.DescriptorSha256 == Provider.DescriptorSha256 && value.AbiSha256 == Provider.AbiSha256 && value.ActivationId == Provider.ActivationId
                && value.ActivationEpoch == Provider.ActivationEpoch && value.SessionGeneration == Provider.SessionGeneration;
        public override bool TryAcquire([NotNullWhen(true)] out SubagentProviderLease<T>? lease)
        {
            lease = null;
            if (Volatile.Read(ref _retired)) return false;
            SunderRpcProviderSnapshot? current;
            try { current = _get(_token); }
            catch (OperationCanceledException) { return false; }
            catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            { if (error.Error.Kind == SunderRpcErrorKind.StaleEndpoint) Retire(); return false; }
            if (current is null || current.State != SunderRpcProviderState.Active || !CanReuse(current)) { Retire(); return false; }
            lock (_gate)
            {
                if (_retired) return false;
                _leases++;
                lease = new(Provider.PackageId, _service, _token, Release);
                return true;
            }
        }
        private void Release() { lock (_gate) { if (--_leases == 0 && _retired) _drained.TrySetResult(); } }
        public void Retire()
        {
            lock (_gate) { if (_retired) return; _retired = true; if (_leases == 0) _drained.TrySetResult(); }
            try { _retirement.Cancel(); }
            catch (AggregateException) { /* Reach all linked work even when an observer fails. */ }
            _ = Cleanup();
        }
        private async Task Cleanup()
        {
            Exception? failure = null;
            try
            {
                await _drained.Task.ConfigureAwait(false);
                if (_service is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            finally { _retirement.Dispose(); }
            if (failure is null) _cleanup.TrySetResult(); else _cleanup.TrySetException(failure);
        }
    }
}

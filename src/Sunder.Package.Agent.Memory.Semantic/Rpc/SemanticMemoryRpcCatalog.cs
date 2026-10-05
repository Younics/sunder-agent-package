using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using CatalogApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Runtime_Catalog;
using EmbeddingApi = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Embedding_Provider;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

public sealed class SemanticMemoryRpcCatalog : ISemanticMemoryRpcCatalog, ISunderWorkerLifecycleService, IAsyncDisposable
{
    private readonly ISunderRpcClient _rpc;
    private readonly CatalogApi.Contract _catalog;
    private readonly EmbeddingApi.Contract _embeddings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Reference<IAgentRuntimeCatalog>> _catalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reference<IAgentEmbeddingProvider>> _providers = new(StringComparer.Ordinal);
    private readonly List<Task> _retirements = [];
    private Task _completion = Task.CompletedTask;
    private Task? _stopTask;
    private bool _started;
    private bool _stopped;

    public SemanticMemoryRpcCatalog(ISunderRpcClient rpc)
    {
        _rpc = rpc;
        var api = new AgentPackageApi(rpc);
        _catalog = api.CSunder_Agent_Runtime_Catalog;
        _embeddings = api.CSunder_Agent_Embedding_Provider;
    }

    public event Action? Changed;
    public Task Completion { get { lock (_gate) return _completion; } }
    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask CommitGenerationAsync(SunderWorkerGenerationContext generation, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default) => StartAsync(cancellationToken);
    public ValueTask ShutdownAsync(SunderWorkerShutdownContext context, CancellationToken cancellationToken = default) => DisposeAsync();

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) return ValueTask.CompletedTask;
            _started = true;
            _completion = Task.WhenAll(Task.Run(() => WatchAsync(WatchCatalogs, _catalogs)), Task.Run(() => WatchAsync(WatchEmbeddings, _providers)));
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<SemanticProviderReference<IAgentRuntimeCatalog>> GetRuntimeCatalogs()
    {
        if (!IsStarted()) return [];
        try
        {
            var snapshot = _catalog.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_catalogs, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => _catalog.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new GeneratedRuntimeCatalogClient(_rpc, _catalog, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_catalogs); return []; }
    }

    public IReadOnlyList<SemanticProviderReference<IAgentEmbeddingProvider>> GetEmbeddingProviders()
    {
        if (!IsStarted()) return [];
        try
        {
            var snapshot = _embeddings.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_providers, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => _embeddings.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new GeneratedEmbeddingClient(_rpc, _embeddings, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_providers); return []; }
    }

    private bool IsStarted()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopped, this); return _started; }
    }

    private IReadOnlyList<SemanticProviderReference<T>> Reconcile<T, TProvider>(Dictionary<string, Reference<T>> known,
        (SunderRpcProviderSnapshot Snapshot, TProvider Value)[] providers, bool reset, Func<TProvider, Reference<T>> create) where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var active = providers.Where(value => value.Snapshot.State == SunderRpcProviderState.Active).ToArray();
            var endpoints = active.Select(value => value.Snapshot.Endpoint.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in known.Where(pair => reset || !endpoints.Contains(pair.Key)).ToArray())
            { Retire(pair.Value); known.Remove(pair.Key); }
            var result = new List<SemanticProviderReference<T>>(active.Length);
            foreach (var provider in active)
            {
                var key = provider.Snapshot.Endpoint.Value;
                if (!known.TryGetValue(key, out var reference) || !reference.CanReuse(provider.Snapshot))
                {
                    if (reference is not null) Retire(reference);
                    reference = create(provider.Value);
                    known[key] = reference;
                }
                result.Add(reference);
            }
            return result;
        }
    }

    private async IAsyncEnumerable<Change> WatchCatalogs(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in _catalog.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(item.Revision, item.Sequence, item.Kind, item.Provider?.Snapshot);
    }
    private async IAsyncEnumerable<Change> WatchEmbeddings(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in _embeddings.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(item.Revision, item.Sequence, item.Kind, item.Provider?.Snapshot);
    }
    private async Task WatchAsync<T>(Func<long, long, CancellationToken, IAsyncEnumerable<Change>> watch, Dictionary<string, Reference<T>> known) where T : class
    {
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in watch(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    lock (_gate)
                    {
                        if (change.Kind == SunderRpcCatalogEventKind.ResetRequired)
                        {
                            foreach (var reference in known.Values) Retire(reference);
                            known.Clear();
                        }
                        else if (change.Provider is { } provider && known.TryGetValue(provider.Endpoint.Value, out var reference)
                            && (change.Kind is SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Deactivated or SunderRpcCatalogEventKind.Faulted
                                || !reference.CanReuse(provider)))
                        {
                            Retire(reference); known.Remove(provider.Endpoint.Value);
                        }
                    }
                    RaiseChanged();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { Invalidate(known); }
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }

    private void Invalidate<T>(Dictionary<string, Reference<T>> known) where T : class
    {
        lock (_gate) { foreach (var reference in known.Values) Retire(reference); known.Clear(); }
        RaiseChanged();
    }
    private void Retire<T>(Reference<T> reference) where T : class
    {
        _retirements.RemoveAll(task => task.IsCompletedSuccessfully);
        _retirements.Add(reference.Retire());
    }
    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { /* Projection observers cannot interrupt provider retirement. */ }
        }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource stopped;
        Task[] work;
        lock (_gate)
        {
            if (_stopTask is not null) return new(_stopTask);
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = stopped.Task;
            _stopped = true;
            foreach (var reference in _catalogs.Values) Retire(reference);
            foreach (var reference in _providers.Values) Retire(reference);
            _catalogs.Clear(); _providers.Clear();
            work = _retirements.Append(_completion).ToArray();
        }
        _ = StopAsync(work, stopped);
        return new(stopped.Task);
    }
    private async Task StopAsync(Task[] work, TaskCompletionSource stopped)
    {
        Exception? failure = null;
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(work).ConfigureAwait(false); }
        }
        catch (Exception error) { failure = error; }
        finally { _lifetime.Dispose(); }
        if (failure is null) stopped.TrySetResult();
        else stopped.TrySetException(failure);
    }

    private sealed record Change(long Revision, long Sequence, SunderRpcCatalogEventKind Kind, SunderRpcProviderSnapshot? Provider);
    private sealed class Reference<T> : SemanticProviderReference<T> where T : class
    {
        private readonly Func<CancellationToken, SunderRpcProviderSnapshot?> _get;
        private readonly CancellationTokenSource _retirement = new();
        private readonly CancellationToken _token;
        private readonly T _service;
        private int _retired;
        private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override SunderRpcProviderSnapshot Provider { get; }

        internal Reference(SunderRpcProviderSnapshot provider, Func<CancellationToken, SunderRpcProviderSnapshot?> get, Func<CancellationToken, T> create)
        {
            Provider = provider; _get = get; _token = _retirement.Token; _service = create(_token);
        }
        internal bool CanReuse(SunderRpcProviderSnapshot current)
            => Volatile.Read(ref _retired) == 0 && current.Endpoint == Provider.Endpoint && current.ActivationId == Provider.ActivationId
                && current.ActivationEpoch == Provider.ActivationEpoch && current.SessionGeneration == Provider.SessionGeneration
                && current.PackageId == Provider.PackageId && current.PackageVersion == Provider.PackageVersion && current.ProviderId == Provider.ProviderId
                && current.ContractId == Provider.ContractId && current.ContractVersion == Provider.ContractVersion
                && current.DescriptorSha256 == Provider.DescriptorSha256 && current.AbiSha256 == Provider.AbiSha256;
        public override bool TryAcquire([NotNullWhen(true)] out SemanticProviderLease<T>? lease)
        {
            lease = null;
            if (Volatile.Read(ref _retired) != 0) return false;
            SunderRpcProviderSnapshot? current;
            try { current = _get(_token); }
            catch (OperationCanceledException) { return false; }
            catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                if (error.Error.Kind == SunderRpcErrorKind.StaleEndpoint) _ = Retire();
                return false;
            }
            if (current is null || current.State != SunderRpcProviderState.Active || !CanReuse(current)) { _ = Retire(); return false; }
            lease = new(Provider.PackageId, _service, _token);
            return true;
        }
        internal Task Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return _cleanup.Task;
            try { _retirement.Cancel(); }
            catch (AggregateException) { /* Notify all observers even if one throws. */ }
            _ = Cleanup();
            return _cleanup.Task;
        }
        private async Task Cleanup()
        {
            Exception? failure = null;
            try
            {
                if (_service is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (_service is IDisposable disposable) disposable.Dispose();
            }
            catch (Exception error) { failure = error; }
            finally { _retirement.Dispose(); }
            if (failure is null) _cleanup.TrySetResult();
            else _cleanup.TrySetException(failure);
        }
    }
}

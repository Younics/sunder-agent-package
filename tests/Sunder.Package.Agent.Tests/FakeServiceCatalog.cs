using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Tests;

// Domain-only fixture. Service calls are the supplied local fake, while readiness,
// currentness and retirement are real SDK binding behavior. RPC dispatch is forbidden.
internal sealed class FakeServiceCatalog<TService> : IServiceProviders<TService>, IAsyncDisposable where TService : class
{
    private readonly Visibility _visibility = new();
    private readonly SunderRpcServiceBindingLifetime _bindings;
    private readonly SunderRpcContractDescriptor _contract;
    private readonly Dictionary<string, TService> _services = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal FakeServiceCatalog(SunderRpcContractDescriptor contract)
    {
        _contract = contract;
        _bindings = new SunderRpcServiceBindingLifetime(_visibility);
    }

    internal ServiceProvider<TService> Add(TService service, string packageId = "test.package", string? providerId = null)
    {
        lock (_gate)
        {
            var snapshot = _visibility.Add(packageId, providerId, _contract);
            _services.Add(snapshot.Endpoint.Value, service);
            return _bindings.CreateProvider(snapshot, (_, _, _) => service);
        }
    }

    internal void Retire(ServiceProvider<TService> entry)
        => _visibility.Retire(entry.ActivationId);

    internal void Reset() => _visibility.Reset();
    internal int DiscoveryCount => _visibility.DiscoveryCount;

    public async ValueTask<ServiceProviderCatalog<TService>> ListAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _bindings.DiscoverAsync(_contract.ContractId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return new(snapshot.Revision, snapshot.Sequence,
                snapshot.Providers.Where(provider => provider.State == SunderRpcProviderState.Active)
                    .Select(provider => _bindings.CreateProvider(provider, (_, _, _) => _services[provider.Endpoint.Value])), snapshot.ResetRequired);
    }

    public async IAsyncEnumerable<ServiceProviderCatalogEvent<TService>> WatchAsync(long afterRevision, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in _visibility.WatchAsync(afterRevision, afterSequence, cancellationToken).ConfigureAwait(false))
        {
            ServiceProvider<TService>? entry = null;
            if (change.Provider is { } provider)
                lock (_gate)
                    if (_services.TryGetValue(provider.Endpoint.Value, out var service))
                        entry = provider.State == SunderRpcProviderState.Active
                            ? _bindings.CreateProvider(provider, (_, _, _) => service)
                            : new ServiceProvider<TService>(provider, service);
            yield return new(change.Revision, change.Sequence, (ServiceProviderChangeKind)change.Kind, entry);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _bindings.DisposeAsync().ConfigureAwait(false);
        _visibility.Dispose();
    }

    private sealed class Visibility : ISunderRpcClient, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SunderRpcProviderSnapshot> _providers = new(StringComparer.Ordinal);
        private readonly List<SunderRpcCatalogEvent> _history = [];
        private readonly List<Channel<SunderRpcCatalogEvent>> _watchers = [];
        private long _revision;
        private long _sequence;
        private bool _disposed;
        private int _discoveryCount;
        private int _providerSequence;

        internal int DiscoveryCount => Volatile.Read(ref _discoveryCount);

        internal SunderRpcProviderSnapshot Add(string packageId, string? providerId, SunderRpcContractDescriptor contract)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var activation = Guid.NewGuid();
                var provider = new SunderRpcProviderSnapshot(packageId, "1.0.0", providerId ?? $"test.provider.{++_providerSequence}",
                    contract.ContractId, contract.Version, contract.Sha256,
                    contract.AbiSha256 ?? throw new InvalidOperationException("The reviewed test contract must carry its ABI hash."),
                    activation, 1, 1, new(activation.ToString("N")), ++_revision, SunderRpcProviderState.Active);
                _providers.Add(provider.Endpoint.Value, provider);
                Publish(new(_revision, ++_sequence, SunderRpcCatalogEventKind.Added, provider));
                return provider;
            }
        }

        internal void Retire(Guid activation)
        {
            lock (_gate)
            {
                var provider = _providers.Values.SingleOrDefault(value => value.ActivationId == activation);
                if (provider is null) return;
                _providers.Remove(provider.Endpoint.Value);
                Publish(new(++_revision, ++_sequence, SunderRpcCatalogEventKind.Removed,
                    provider with { CatalogRevision = _revision, State = SunderRpcProviderState.Inactive }));
            }
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _history.Clear();
                Publish(new(++_revision, ++_sequence, SunderRpcCatalogEventKind.ResetRequired, null));
            }
        }

        private void Publish(SunderRpcCatalogEvent change)
        {
            _history.Add(change);
            if (_history.Count > 128) _history.RemoveAt(0);
            foreach (var watcher in _watchers) watcher.Writer.TryWrite(change);
        }

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _discoveryCount);
            lock (_gate)
                return ValueTask.FromResult(new SunderRpcCatalogSnapshot(_revision, _sequence,
                    _providers.Values.Where(provider => string.IsNullOrEmpty(contractId) || provider.ContractId == contractId)));
        }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) return ValueTask.FromResult(_providers.GetValueOrDefault(endpoint.Value));
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateUnbounded<SunderRpcCatalogEvent>();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var exactCursor = afterRevision == _revision && afterSequence == _sequence
                    || afterRevision == 0 && afterSequence == 0 && (_history.Count == 0 || _history[0].Sequence == 1)
                    || _history.Any(change => change.Revision == afterRevision && change.Sequence == afterSequence);
                if (!exactCursor)
                    channel.Writer.TryWrite(new(_revision, _sequence, SunderRpcCatalogEventKind.ResetRequired, null));
                else foreach (var change in _history.Where(change => change.Sequence > afterSequence)) channel.Writer.TryWrite(change);
                _watchers.Add(channel);
            }
            try
            {
                await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return change;
            }
            finally
            {
                lock (_gate) _watchers.Remove(channel);
                channel.Writer.TryComplete();
            }
        }

        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_providers.TryGetValue(endpoint.Value, out var provider)) return ValueTask.FromResult(false);
                Retire(provider.ActivationId);
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Domain fixtures call typed local services, not an RPC substitute.");

        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Generated-client integration uses the regression broker.");

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var watcher in _watchers) watcher.Writer.TryComplete();
                _watchers.Clear();
            }
        }
    }
}

using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Contracts.Services;

/// <summary>
/// Aggregates RPC catalog and provider-specific selectable-capability invalidations.
/// </summary>
/// <remarks>
/// <para>
/// The observer operates within the role represented by the supplied RPC catalog. It subscribes to every current
/// <see cref="IAgentProfileSelectableCapabilityChangeNotifier"/> registered at
/// the selectable-capability provider contract and also watches RPC catalog revisions.
/// </para>
/// <para>
/// Provider subscriptions are deduplicated by object identity, not provider identifier, and no ordering,
/// revision, or coalescing guarantee is added to <see cref="Changed"/>. The host continues to own provider
/// instances; this object owns only its subscriptions and must be disposed before its consumer is released.
/// </para>
/// <para>
/// Subscription bookkeeping is synchronized, but notifications are raised synchronously on the originating
/// thread and can race with refresh or disposal. There is no cancellation channel or subscriber-exception
/// isolation. The event carries no trusted data and must be treated only as a request to rebuild a catalog;
/// consumers must repeat normal descriptor validation, package-provenance, and permission checks.
/// </para>
/// </remarks>
public sealed class AgentProfileSelectableCapabilityChangeObserver : IDisposable
{
    private readonly AgentRpcCatalog _catalog;
    private readonly object _syncRoot = new();
    private readonly List<ProviderSubscription> _providerSubscriptions = [];
    private bool _disposed;

    /// <summary>
    /// Initializes an observer and subscribes to the catalog and its current notifying providers.
    /// </summary>
    /// <param name="catalog">
    /// The role-local RPC catalog to observe. The observer does not dispose it.
    /// </param>
    public AgentProfileSelectableCapabilityChangeObserver(AgentRpcCatalog catalog)
    {
        _catalog = catalog;
        _catalog.Changed += OnCatalogChanged;
        RefreshProviderSubscriptions();
    }

    /// <summary>
    /// Occurs when any Agent RPC catalog projection or provider-specific selectable capabilities may have changed.
    /// </summary>
    /// <remarks>
    /// Every RPC catalog transition is forwarded because consumers commonly cache combined provider, tool, model,
    /// execution-target, behavior-loop, and selectable-capability projections. Handlers run synchronously on the
    /// publishing thread. Notifications can be repeated or concurrent,
    /// have no payload, and do not guarantee that the resulting descriptor set is different.
    /// </remarks>
    public event Action? Changed;

    /// <summary>
    /// Reconciles provider event subscriptions with a fresh extension-catalog snapshot.
    /// </summary>
    /// <remarks>
    /// The method is thread-safe and idempotent for the same provider object set. It does not raise
    /// <see cref="Changed"/> itself and becomes a no-op after disposal. Catalog access is synchronous and
    /// has no cancellation or per-provider failure boundary.
    /// </remarks>
    public void RefreshProviderSubscriptions()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            var currentSubscriptions = new HashSet<ProviderSubscription>();
            foreach (var reference in _catalog.GetServiceReferences(AgentRpcServices.SelectableCapabilityProviders))
            {
                if (!reference.TryAcquire(out var lease))
                {
                    continue;
                }

                if (lease.Service is not IAgentProfileSelectableCapabilityChangeNotifier notifier
                    || lease.RetirementToken.IsCancellationRequested)
                {
                    lease.Dispose();
                    continue;
                }

                var existing = _providerSubscriptions.FirstOrDefault(subscription =>
                    ReferenceEquals(subscription.Notifier, notifier));
                if (existing is not null)
                {
                    currentSubscriptions.Add(existing);
                    lease.Dispose();
                    continue;
                }

                var added = new ProviderSubscription(lease, notifier);
                notifier.SelectableCapabilitiesChanged += OnSelectableCapabilitiesChanged;
                _providerSubscriptions.Add(added);
                currentSubscriptions.Add(added);
            }

            foreach (var subscription in _providerSubscriptions
                         .Where(subscription => !currentSubscriptions.Contains(subscription))
                         .ToArray())
            {
                subscription.Notifier.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
                _providerSubscriptions.Remove(subscription);
                subscription.Dispose();
            }
        }
    }

    private void OnCatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs e)
    {
        var selectableProvidersChanged = e.IncludesContract(AgentRpcContractIds.SelectableCapabilityProvider);
        if (selectableProvidersChanged) RefreshProviderSubscriptions();
        Changed?.Invoke();
    }

    private void OnSelectableCapabilitiesChanged()
        => Changed?.Invoke();

    /// <summary>
    /// Unsubscribes from the catalog monitor and all currently tracked providers.
    /// </summary>
    /// <remarks>
    /// Disposal is idempotent and does not dispose the catalog or provider instances. A notification
    /// already in progress can still complete on its publishing thread.
    /// </remarks>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _catalog.Changed -= OnCatalogChanged;

            foreach (var subscription in _providerSubscriptions)
            {
                subscription.Notifier.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
                subscription.Dispose();
            }

            _providerSubscriptions.Clear();
        }
    }

    private sealed class ProviderSubscription(
        AgentRpcLease<IAgentProfileSelectableCapabilityProvider> lease,
        IAgentProfileSelectableCapabilityChangeNotifier notifier) : IDisposable
    {
        private AgentRpcLease<IAgentProfileSelectableCapabilityProvider>? _lease = lease;

        internal IAgentProfileSelectableCapabilityChangeNotifier Notifier { get; } = notifier;

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts.Services;

/// <summary>
/// Aggregates extension-catalog and provider-specific selectable-capability invalidations.
/// </summary>
/// <remarks>
/// <para>
/// The observer operates within the App or Runtime role represented by the supplied catalog; catalogs
/// are isolated, so it never bridges contributions between roles. It subscribes to every current
/// <see cref="IAgentProfileSelectableCapabilityChangeNotifier"/> registered at
/// <see cref="PackageExtensionPoints.ProfileSelectableCapabilityProviders"/> and also watches catalog
/// revisions when the catalog implements <see cref="IPackageExtensionCatalogMonitor"/>.
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
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageExtensionInvocationCatalog _invocationCatalog;
    private readonly IPackageExtensionCatalogMonitor? _extensionCatalogMonitor;
    private readonly object _syncRoot = new();
    private readonly List<ProviderSubscription> _providerSubscriptions = [];
    private bool _disposed;

    /// <summary>
    /// Initializes an observer and subscribes to the catalog and its current notifying providers.
    /// </summary>
    /// <param name="extensionCatalog">
    /// The role-local, host-owned extension catalog to observe. The observer does not dispose it.
    /// </param>
    public AgentProfileSelectableCapabilityChangeObserver(IPackageExtensionCatalog extensionCatalog)
    {
        _extensionCatalog = extensionCatalog;
        _invocationCatalog = extensionCatalog as IPackageExtensionInvocationCatalog
            ?? throw new InvalidOperationException(
                "The host extension catalog does not support activation-scoped invocation leases.");
        if (_extensionCatalog is IPackageExtensionCatalogMonitor monitor)
        {
            _extensionCatalogMonitor = monitor;
            monitor.Changed += OnExtensionCatalogChanged;
        }
        RefreshProviderSubscriptions();
    }

    /// <summary>
    /// Occurs when catalog membership or a provider notification may have changed selectable capabilities.
    /// </summary>
    /// <remarks>
    /// Handlers run synchronously on the publishing thread. Notifications can be repeated or concurrent,
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
            foreach (var reference in _invocationCatalog.GetExtensionReferences(
                         PackageExtensionPoints.ProfileSelectableCapabilityProviders))
            {
                if (!reference.TryAcquire(out var lease))
                {
                    continue;
                }

                if (lease.Contribution is not IAgentProfileSelectableCapabilityChangeNotifier notifier
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

    private void OnExtensionCatalogChanged(object? sender, PackageExtensionCatalogChangedEventArgs e)
    {
        RefreshProviderSubscriptions();
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
            if (_extensionCatalogMonitor is not null)
            {
                _extensionCatalogMonitor.Changed -= OnExtensionCatalogChanged;
            }

            foreach (var subscription in _providerSubscriptions)
            {
                subscription.Notifier.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
                subscription.Dispose();
            }

            _providerSubscriptions.Clear();
        }
    }

    private sealed class ProviderSubscription(
        IPackageExtensionLease<IAgentProfileSelectableCapabilityProvider> lease,
        IAgentProfileSelectableCapabilityChangeNotifier notifier) : IDisposable
    {
        private IPackageExtensionLease<IAgentProfileSelectableCapabilityProvider>? _lease = lease;

        internal IAgentProfileSelectableCapabilityChangeNotifier Notifier { get; } = notifier;

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

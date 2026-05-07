using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts.Services;

public sealed class AgentProfileSelectableCapabilityChangeObserver : IDisposable
{
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageExtensionCatalogChangeNotifier? _extensionCatalogChangeNotifier;
    private readonly object _syncRoot = new();
    private readonly HashSet<IAgentProfileSelectableCapabilityChangeNotifier> _subscribedProviders = [];
    private bool _disposed;

    public AgentProfileSelectableCapabilityChangeObserver(IPackageExtensionCatalog extensionCatalog)
    {
        _extensionCatalog = extensionCatalog;
        if (_extensionCatalog is IPackageExtensionCatalogChangeNotifier changeNotifier)
        {
            _extensionCatalogChangeNotifier = changeNotifier;
            changeNotifier.ExtensionsChanged += OnExtensionCatalogChanged;
        }

        RefreshProviderSubscriptions();
    }

    public event Action? Changed;

    public void RefreshProviderSubscriptions()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            var currentProviders = _extensionCatalog.GetExtensions(PackageExtensionPoints.ProfileSelectableCapabilityProviders)
                .OfType<IAgentProfileSelectableCapabilityChangeNotifier>()
                .ToHashSet();
            foreach (var provider in _subscribedProviders.Except(currentProviders).ToArray())
            {
                provider.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
                _subscribedProviders.Remove(provider);
            }

            foreach (var provider in currentProviders)
            {
                if (_subscribedProviders.Add(provider))
                {
                    provider.SelectableCapabilitiesChanged += OnSelectableCapabilitiesChanged;
                }
            }
        }
    }

    private void OnExtensionCatalogChanged()
    {
        RefreshProviderSubscriptions();
        Changed?.Invoke();
    }

    private void OnSelectableCapabilitiesChanged()
        => Changed?.Invoke();

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_extensionCatalogChangeNotifier is not null)
            {
                _extensionCatalogChangeNotifier.ExtensionsChanged -= OnExtensionCatalogChanged;
            }

            foreach (var provider in _subscribedProviders)
            {
                provider.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
            }

            _subscribedProviders.Clear();
        }
    }
}

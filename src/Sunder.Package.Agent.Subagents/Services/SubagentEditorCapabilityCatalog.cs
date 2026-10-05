using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed partial class SubagentEditorCapabilityCatalog : IDisposable
{
    private readonly ISubagentRuntimeBindings _bindings;
    private readonly object _subscriptionGate = new();
    private readonly List<ProviderSubscription> _providerSubscriptions = [];
    private bool _disposed;

    public SubagentEditorCapabilityCatalog(ISubagentRuntimeBindings bindings)
    {
        _bindings = bindings;
        _bindings.CatalogChanged += OnCatalogChanged;
        RefreshProviderSubscriptions();
    }

    public event Action? Changed;

    internal void RefreshSubscriptions() => RefreshProviderSubscriptions();

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = new AgentToolSourceContext(
            SessionId: null,
            Profile: null,
            Workspace: null,
            ExecutionBinding: null);
        var descriptors = new List<AgentToolDescriptor>();
        var sources = SubagentCatalogRpcInvocation.Snapshot(
            _bindings.GetToolSources,
            cancellationToken,
            static source => source.DisplayName);
        foreach (var source in sources.OrderBy(
                     static item => item.Metadata,
                     StringComparer.OrdinalIgnoreCase))
        {
            var contributed = await SubagentCatalogRpcInvocation.InvokeOptionalAsync(
                source,
                cancellationToken,
                (source, token) => source.ListToolsAsync(context, token)).ConfigureAwait(false);
            if (contributed.IsAvailable)
            {
                descriptors.AddRange(contributed.Value);
            }
        }

        return descriptors
            .Where(descriptor => descriptor.SelectionScope == AgentToolSelectionScope.Tool)
            .GroupBy(
                descriptor => string.Concat(
                    descriptor.SourceId ?? string.Empty,
                    "\n",
                    descriptor.ToolId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            RefreshProviderSubscriptions();
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw SubagentCatalogRpcInvocation.Cancelled(exception, cancellationToken);
        }
        var request = new AgentProfileSelectableCapabilityRequest(Profile: null);
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        var providers = SubagentCatalogRpcInvocation.Snapshot(
            _bindings.GetSelectableCapabilityProviders,
            cancellationToken,
            static provider => provider.DisplayName);
        foreach (var provider in providers
                     .OrderBy(provider => provider.Metadata, StringComparer.OrdinalIgnoreCase))
        {
            var contributed = await SubagentCatalogRpcInvocation.InvokeOptionalAsync(
                provider,
                cancellationToken,
                (contribution, token) => contribution.ListCapabilitiesAsync(request, token)).ConfigureAwait(false);
            if (contributed.IsAvailable)
            {
                capabilities.AddRange(contributed.Value);
            }
        }

        return capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Kind)
                && !string.IsNullOrWhiteSpace(capability.CapabilityId)
                && !string.IsNullOrWhiteSpace(capability.DisplayName))
            .GroupBy(
                capability => string.Concat(
                    capability.Kind,
                    "\n",
                    capability.SourceId ?? string.Empty,
                    "\n",
                    capability.CapabilityId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(capability => capability.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void OnCatalogChanged(SubagentCatalogChange change)
    {
        if ((change & (SubagentCatalogChange.ChatProviders | SubagentCatalogChange.Tools
            | SubagentCatalogChange.SelectableCapabilities | SubagentCatalogChange.RuntimeCatalog)) == 0)
        {
            return;
        }

        if ((change & SubagentCatalogChange.SelectableCapabilities) != 0)
        {
            RefreshProviderSubscriptions();
        }
        Changed?.Invoke();
    }

    private void RefreshProviderSubscriptions()
    {
        lock (_subscriptionGate)
        {
            if (_disposed)
            {
                return;
            }

            var currentSubscriptions = new HashSet<ProviderSubscription>();
            foreach (var reference in _bindings.GetSelectableCapabilityProviders())
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
                added.ObserveRetirement(() => OnProviderRetired(added));
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

    private void OnSelectableCapabilitiesChanged()
        => Changed?.Invoke();

    private void OnProviderRetired(ProviderSubscription subscription)
    {
        lock (_subscriptionGate)
        {
            if (!_providerSubscriptions.Remove(subscription)) return;
            subscription.Notifier.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
            subscription.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_subscriptionGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bindings.CatalogChanged -= OnCatalogChanged;
            foreach (var subscription in _providerSubscriptions)
            {
                subscription.Notifier.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
                subscription.Dispose();
            }
            _providerSubscriptions.Clear();
        }
    }

    private sealed class ProviderSubscription(
        SubagentProviderLease<IAgentProfileSelectableCapabilityProvider> lease,
        IAgentProfileSelectableCapabilityChangeNotifier notifier) : IDisposable
    {
        private SubagentProviderLease<IAgentProfileSelectableCapabilityProvider>? _lease = lease;
        private readonly CancellationToken _retirementToken = lease.RetirementToken;
        private CancellationTokenRegistration _retirement;

        internal IAgentProfileSelectableCapabilityChangeNotifier Notifier { get; } = notifier;

        internal void ObserveRetirement(Action retired)
        {
            _retirement = _retirementToken.Register(retired);
            if (_lease is null) _retirement.Unregister();
        }
        public void Dispose()
        {
            _retirement.Unregister();
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }
}

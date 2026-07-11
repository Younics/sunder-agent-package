using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentEditorCapabilityCatalog : IDisposable
{
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly AgentProfileSelectableCapabilityChangeObserver _changeObserver;
    private bool _disposed;

    public SubagentEditorCapabilityCatalog(IPackageExtensionCatalog extensionCatalog)
    {
        _extensionCatalog = extensionCatalog;
        _changeObserver = new AgentProfileSelectableCapabilityChangeObserver(extensionCatalog);
        _changeObserver.Changed += OnChanged;
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = new AgentToolSourceContext(
            SessionId: null,
            Profile: null,
            Workspace: null,
            ExecutionBinding: null);
        var descriptors = new List<AgentToolDescriptor>();
        descriptors.AddRange(_extensionCatalog
            .GetExtensions(PackageExtensionPoints.Tools)
            .Select(tool => tool.Descriptor));
        foreach (var source in _extensionCatalog.GetExtensions(PackageExtensionPoints.ToolSources))
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptors.AddRange(await source
                .ListToolsAsync(context, cancellationToken)
                .ConfigureAwait(false));
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
        _changeObserver.RefreshProviderSubscriptions();
        var request = new AgentProfileSelectableCapabilityRequest(Profile: null);
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        foreach (var provider in _extensionCatalog
            .GetExtensions(PackageExtensionPoints.ProfileSelectableCapabilityProviders)
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            capabilities.AddRange(await provider
                .ListCapabilitiesAsync(request, cancellationToken)
                .ConfigureAwait(false));
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

    private void OnChanged() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _changeObserver.Changed -= OnChanged;
        _changeObserver.Dispose();
    }
}

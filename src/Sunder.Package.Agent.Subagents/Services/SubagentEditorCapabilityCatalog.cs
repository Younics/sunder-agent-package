using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentEditorCapabilityCatalog : IDisposable
{
    private readonly AgentRpcCatalog _rpcCatalog;
    private readonly AgentProfileSelectableCapabilityChangeObserver _changeObserver;
    private bool _disposed;

    public SubagentEditorCapabilityCatalog(AgentRpcCatalog rpcCatalog)
    {
        _rpcCatalog = rpcCatalog;
        _changeObserver = new AgentProfileSelectableCapabilityChangeObserver(rpcCatalog);
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
        var sources = SubagentCatalogRpcInvocation.Snapshot(
            _rpcCatalog,
            AgentRpcServices.ToolSources,
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
            _changeObserver.RefreshProviderSubscriptions();
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw SubagentCatalogRpcInvocation.Cancelled(exception, cancellationToken);
        }
        var request = new AgentProfileSelectableCapabilityRequest(Profile: null);
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        var providers = SubagentCatalogRpcInvocation.Snapshot(
            _rpcCatalog,
            AgentRpcServices.SelectableCapabilityProviders,
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

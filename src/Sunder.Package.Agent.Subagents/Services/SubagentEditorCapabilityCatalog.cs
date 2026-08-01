using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Protocol;

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
        foreach (var reference in _rpcCatalog.GetServiceReferences(AgentRpcServices.ToolSources))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contributed = await InvokeAsync(
                reference,
                cancellationToken,
                (source, token) => source.ListToolsAsync(context, token)).ConfigureAwait(false);
            if (contributed is not null)
            {
                descriptors.AddRange(contributed);
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
        _changeObserver.RefreshProviderSubscriptions();
        var request = new AgentProfileSelectableCapabilityRequest(Profile: null);
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        var providers = SnapshotCapabilityProviders();
        foreach (var provider in providers
                     .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var contributed = await InvokeAsync(
                provider.Reference,
                cancellationToken,
                (contribution, token) => contribution.ListCapabilitiesAsync(request, token)).ConfigureAwait(false);
            if (contributed is not null)
            {
                capabilities.AddRange(contributed);
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

    private IReadOnlyList<CapabilityProviderReference> SnapshotCapabilityProviders()
    {
        var providers = new List<CapabilityProviderReference>();
        foreach (var reference in _rpcCatalog.GetServiceReferences(AgentRpcServices.SelectableCapabilityProviders))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    providers.Add(new CapabilityProviderReference(reference, lease.Service.DisplayName));
                }
            }
        }

        return providers;
    }

    private static async ValueTask<TResult?> InvokeAsync<TContract, TResult>(
        AgentRpcReference<TContract> reference,
        CancellationToken cancellationToken,
        Func<TContract, CancellationToken, ValueTask<TResult>> callback)
        where TContract : class
    {
        if (!reference.TryAcquire(out var lease))
        {
            return default;
        }

        using (lease)
        {
            var retirementToken = lease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
                return retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? default
                    : result;
            }
            catch (OperationCanceledException) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                return default;
            }
        }
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

    private sealed record CapabilityProviderReference(
        AgentRpcReference<IAgentProfileSelectableCapabilityProvider> Reference,
        string DisplayName);
}

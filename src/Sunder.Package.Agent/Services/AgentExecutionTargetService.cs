using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetService(
    IServiceProviders<AgentServices.IExecutionTargetService> providers) : IAgentExecutionGateway
{
    public async Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(
        CancellationToken cancellationToken = default)
        => (await SnapshotTargetsAsync(omitUnavailable: true, cancellationToken).ConfigureAwait(false))
            .Select(static target => target.Descriptor)
            .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal async ValueTask<ServiceProvider<AgentServices.IExecutionTargetService>?> ResolveTargetAsync(
        AgentWorkspaceBindingRecord? binding,
        CancellationToken cancellationToken = default)
    {
        if (binding is null || !binding.IsEnabled)
        {
            return null;
        }

        var targets = await SnapshotTargetsAsync(omitUnavailable: false, cancellationToken)
            .ConfigureAwait(false);
        foreach (var target in targets)
        {
            if (string.Equals(target.Descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.Descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase))
            {
                return target.Provider;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<(ServiceProvider<AgentServices.IExecutionTargetService> Provider, AgentExecutionTargetDescriptor Descriptor)>> SnapshotTargetsAsync(
        bool omitUnavailable,
        CancellationToken cancellationToken)
    {
        var targets = new List<(ServiceProvider<AgentServices.IExecutionTargetService>, AgentExecutionTargetDescriptor)>();
        var catalog = await providers.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                targets.Add((provider, descriptor with { Facets = descriptor.Facets.ToArray() }));
            }
            catch (SunderRpcException exception) when (omitUnavailable && !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // Catalog presentation omits targets withdrawn during discovery.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return targets;
    }

    public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
        AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AgentExecutionTargetWarmupResult.Skipped(
            "Warmup requires the Runtime workspace service."));
}

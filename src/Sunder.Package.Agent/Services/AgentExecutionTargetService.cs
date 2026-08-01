using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetService(AgentRpcCatalog rpcCatalog) : IAgentExecutionGateway
{
    public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets()
        => GetTargetReferences()
            .Select(TryDescribe)
            .OfType<AgentExecutionTargetDescriptor>()
            .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal AgentRpcReference<IAgentExecutionTarget>?
        ResolveTargetReference(AgentWorkspaceBindingRecord? binding)
    {
        if (binding is null || !binding.IsEnabled)
        {
            return null;
        }

        foreach (var target in GetTargetReferences())
        {
            var descriptor = TryDescribe(target);
            if (descriptor is not null
                && (string.Equals(descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase)))
            {
                return target;
            }
        }
        return null;
    }

    private IReadOnlyList<AgentRpcReference<IAgentExecutionTarget>> GetTargetReferences()
        => rpcCatalog.GetServiceReferences(AgentRpcServices.ExecutionTargets);

    private static AgentExecutionTargetDescriptor? TryDescribe(AgentRpcReference<IAgentExecutionTarget> target)
    {
        if (!target.TryAcquire(out var lease)) return null;
        using (lease) return lease.Service.Descriptor;
    }

    public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
        AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AgentExecutionTargetWarmupResult.Skipped(
            "Warmup requires the Runtime workspace service."));
}

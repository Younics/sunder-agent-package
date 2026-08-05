using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetService(AgentRpcCatalog rpcCatalog) : IAgentExecutionGateway
{
    internal AgentRpcCatalog RpcCatalog => rpcCatalog;

    public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets()
        => SnapshotTargets(omitUnavailable: true)
            .Select(static target => target.Metadata)
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

        foreach (var target in SnapshotTargets(omitUnavailable: false))
        {
            if (string.Equals(target.Metadata.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.Metadata.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase))
            {
                return target.Reference;
            }
        }
        return null;
    }

    internal async ValueTask<AgentRpcReference<IAgentExecutionTarget>?> ResolveTargetReferenceAsync(
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
            if (string.Equals(target.Metadata.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.Metadata.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase))
            {
                return target.Reference;
            }
        }

        return null;
    }

    private IReadOnlyList<AgentRpcOwnedReference<IAgentExecutionTarget, AgentExecutionTargetDescriptor>>
        SnapshotTargets(bool omitUnavailable)
        => AgentRpcInvocation.Snapshot(
            rpcCatalog,
            AgentRpcServices.ExecutionTargets,
            static target => target.Descriptor with
            {
                Facets = target.Descriptor.Facets.ToArray(),
            },
            omitUnavailable: omitUnavailable);

    private Task<IReadOnlyList<AgentRpcOwnedReference<IAgentExecutionTarget, AgentExecutionTargetDescriptor>>>
        SnapshotTargetsAsync(bool omitUnavailable, CancellationToken cancellationToken)
        => AgentRpcInvocation.SnapshotAsync(
            rpcCatalog,
            AgentRpcServices.ExecutionTargets,
            static (target, token) => DescribeTargetAsync(target, token),
            cancellationToken,
            omitUnavailable);

    private static async ValueTask<AgentExecutionTargetDescriptor> DescribeTargetAsync(
        IAgentExecutionTarget target,
        CancellationToken cancellationToken)
    {
        var descriptor = target is AgentExecutionTargetRpcClient rpcTarget
            ? await rpcTarget.DescribeAsync(cancellationToken).ConfigureAwait(false)
            : target.Descriptor;
        return descriptor with { Facets = descriptor.Facets.ToArray() };
    }

    public Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
        AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AgentExecutionTargetWarmupResult.Skipped(
            "Warmup requires the Runtime workspace service."));
}

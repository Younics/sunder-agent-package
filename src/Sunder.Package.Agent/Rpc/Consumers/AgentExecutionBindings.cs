using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentExecutionBindings
{
    internal static AgentRpcService<IAgentExecutionTarget> Service { get; } =
        AgentRpcService<IAgentExecutionTarget>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedExecutionTargetClient(rpc, provider));

    internal static ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(IAgentExecutionTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return target is GeneratedExecutionTargetClient client ? client.DescribeAsync(token) : ValueTask.FromResult(target.Descriptor);
    }

    internal static bool SupportsFacet(IAgentExecutionTarget target, string facet)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(facet);
        var descriptor = target.Descriptor;
        if (target is GeneratedExecutionTargetClient || descriptor.Facets.Count > 0) return descriptor.SupportsFacet(facet);
        return facet switch
        {
            AgentExecutionFacetIds.ProcessExecution => target is IAgentProcessExecutionTarget,
            AgentExecutionFacetIds.ResourceResolution => target is IAgentExecutionResourceResolver,
            AgentExecutionFacetIds.ResourceAuthority => target is IAgentResourceAuthorityExecutionTarget,
            AgentExecutionFacetIds.ExecutionScope => target is IAgentExecutionScopeProvider,
            AgentExecutionFacetIds.PathMapping => target is IAgentExecutionPathMapper,
            AgentExecutionFacetIds.PathEnvironment => target is IAgentExecutionPathEnvironment,
            AgentExecutionFacetIds.RangedFileRead => target is IAgentRangedFileExecutionTarget,
            AgentExecutionFacetIds.StructuredFileSearch => target is IAgentStructuredFileSearchExecutionTarget,
            AgentExecutionFacetIds.ScopedInstructionDiscovery => target is IAgentScopedInstructionDiscoveryTarget,
            _ => false,
        };
    }
}

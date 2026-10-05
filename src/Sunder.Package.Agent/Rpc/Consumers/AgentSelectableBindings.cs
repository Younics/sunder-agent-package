using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Selectable_Capability_Provider;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentSelectableBindings
{
    internal static AgentRpcService<IAgentProfileSelectableCapabilityProvider> Service { get; } =
        AgentRpcService<IAgentProfileSelectableCapabilityProvider>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedSelectableCapabilityClient(rpc, provider));
}

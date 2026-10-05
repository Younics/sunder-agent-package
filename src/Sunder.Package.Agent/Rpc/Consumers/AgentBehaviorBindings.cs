using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Behavior_Loop;
using ControlApi = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Run_Control;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentBehaviorBindings
{
    internal static AgentRpcProviderService<IAgentBehaviorLoop> Create(AgentRunControlRegistry registrations, AgentRpcCatalog catalog)
        => new(Api.Contract.ContractId, (rpc, provider) => new GeneratedBehaviorLoopClient(rpc, provider, registrations, catalog));

    internal static AgentRpcService<GeneratedRunControlEndpoint> RunControls { get; } =
        AgentRpcService<GeneratedRunControlEndpoint>.WithProviderIdentity(ControlApi.Contract.ContractId,
            static (rpc, provider) => new GeneratedRunControlEndpoint(rpc, provider));
}

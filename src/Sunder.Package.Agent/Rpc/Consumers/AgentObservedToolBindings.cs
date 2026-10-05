using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Tool_Execution_Observed;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentObservedToolBindings
{
    internal static AgentRpcService<IAgentObservedToolExecution> Service { get; } =
        AgentRpcService<IAgentObservedToolExecution>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedObservedToolExecutionClient(rpc, provider));
}

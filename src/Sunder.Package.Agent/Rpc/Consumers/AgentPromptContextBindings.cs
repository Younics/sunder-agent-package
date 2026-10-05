using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Prompt_Context_Contributor;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentPromptContextBindings
{
    internal static AgentRpcService<IAgentPromptContextContributor> Service { get; } =
        AgentRpcService<IAgentPromptContextContributor>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedPromptContextClient(rpc, provider));
}

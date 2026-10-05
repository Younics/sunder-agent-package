using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Chat_Provider;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentChatBindings
{
    internal static AgentRpcService<IAgentChatProvider> Service { get; } =
        AgentRpcService<IAgentChatProvider>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedChatProviderClient(rpc, provider));
}

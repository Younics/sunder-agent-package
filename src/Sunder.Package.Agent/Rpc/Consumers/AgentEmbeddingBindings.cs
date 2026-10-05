using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Embedding_Provider;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentEmbeddingBindings
{
    internal static AgentRpcService<IAgentEmbeddingProvider> Service { get; } =
        AgentRpcService<IAgentEmbeddingProvider>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedEmbeddingProviderClient(rpc, provider));
}

using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Runtime_Catalog;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentToolProfileBindings
{
    internal static AgentRpcService<IAgentToolProfileCatalog> Service { get; } =
        AgentRpcService<IAgentToolProfileCatalog>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedToolProfileCatalogClient(rpc, provider));
}

using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Workspace_Path_Migrator;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentWorkspacePathBindings
{
    internal static AgentRpcService<IAgentWorkspacePathMigrationContributor> Service { get; } =
        AgentRpcService<IAgentWorkspacePathMigrationContributor>.WithProviderIdentity(Api.Contract.ContractId,
            static (rpc, provider) => new GeneratedWorkspacePathMigratorClient(rpc, provider));
}

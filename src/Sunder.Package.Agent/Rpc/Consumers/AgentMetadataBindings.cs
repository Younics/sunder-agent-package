using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Capabilities = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Profile_Capability_Consumer;
using Prompts = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_System_Prompt_Contributor;
using Permissions = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Permission_Surface;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal static class AgentMetadataBindings
{
    internal static AgentRpcService<IAgentProfileCapabilityConsumer> ProfileCapabilityConsumers { get; } =
        AgentRpcService<IAgentProfileCapabilityConsumer>.WithProviderIdentity(Capabilities.Contract.ContractId,
            static (rpc, provider) => new GeneratedProfileCapabilityClient(rpc, provider));

    internal static AgentRpcService<IAgentSystemPromptContributor> SystemPromptContributors { get; } =
        AgentRpcService<IAgentSystemPromptContributor>.WithProviderIdentity(Prompts.Contract.ContractId,
            static (rpc, provider) => new GeneratedSystemPromptClient(rpc, provider));

    internal static AgentRpcService<IAgentPermissionSurface> PermissionSurfaces { get; } =
        AgentRpcService<IAgentPermissionSurface>.WithProviderIdentity(Permissions.Contract.ContractId,
            static (rpc, provider) => new GeneratedPermissionSurfaceClient(rpc, provider));
}

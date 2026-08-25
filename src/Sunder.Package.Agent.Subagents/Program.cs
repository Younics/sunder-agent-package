using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.Protocol;
using Sunder.Package.Agent.Subagents.Protocol.Generated;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;

AgentRpcCatalog? ownedCatalog = null;
ServiceProvider? ownedServices = null;
try
{
    await SunderWorker.RunAsync(context =>
    {
        var catalog = AgentRpcCatalog.CreateDormant(context.Rpc);
        ownedCatalog = catalog;
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton(catalog);
        services.AddSingleton<SubagentStore>();
        services.AddSingleton<SubagentService>();
        services.AddSingleton<SubagentFeature>();
        services.AddSingleton<OrchestratedAgentBehaviorLoop>();
        services.AddSingleton<SubagentStackContributor>();
        services.AddSingleton<SubagentRuntimeHandler>();
        services.AddSingleton<SubagentRuntimeChangeStream>();
        services.AddSingleton<SubagentOwnerProvider>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        ownedServices = serviceProvider;

        var feature = serviceProvider.GetRequiredService<SubagentFeature>();
        var owner = serviceProvider.GetRequiredService<SubagentOwnerProvider>();
        var ownerDescriptor = SubagentsOwnerProtocol.Descriptor;
        return new SunderWorkerOptions(
        [
            Register(
                "subagents.selectable.capabilities",
                AgentRpcContractIds.SelectableCapabilityProvider,
                AgentSelectableCapabilityProviderRpc.CreateHandler(feature)),
            Register(
                "subagents.tools",
                AgentRpcContractIds.ToolSource,
                AgentToolSourceRpc.CreateHandler(feature)),
            Register(
                "subagents.prompt.context",
                AgentRpcContractIds.PromptContextContributor,
                AgentPromptContextContributorRpc.CreateHandler(feature)),
            Register(
                "subagents.behavior.loop",
                AgentRpcContractIds.BehaviorLoop,
                AgentBehaviorLoopRpc.CreateHandler(
                    serviceProvider.GetRequiredService<OrchestratedAgentBehaviorLoop>(),
                    catalog)),
            new SunderWorkerProviderRegistration(
                "subagents.stack",
                SunderStackContributorRpc.ContractId,
                SunderStackContributorRpc.ContractVersion,
                SunderStackContributorRpc.DescriptorSha256,
                SunderStackContributorRpc.AbiSha256,
                SunderStackContributorRpc.CreateHandler(
                    serviceProvider.GetRequiredService<SubagentStackContributor>())),
            new SunderWorkerProviderRegistration(
                SubagentsOwnerProtocol.ProviderId,
                ownerDescriptor.ContractId,
                ownerDescriptor.Version,
                ownerDescriptor.Sha256,
                ownerDescriptor.AbiSha256 ?? throw new InvalidDataException(
                    "The Subagents owner descriptor is missing its V2 ABI identity."),
                new SunderPackageAgentSubagentsOwnerRpcHandler(owner)),
        ])
        {
            OnActivated = (_, _) =>
            {
                serviceProvider.GetRequiredService<SubagentStore>().Activate();
                catalog.StartWatching();
                return ValueTask.CompletedTask;
            },
            OnShutdown = async (_, _) =>
            {
                catalog.Dispose();
                if (ReferenceEquals(ownedCatalog, catalog)) ownedCatalog = null;
                await serviceProvider.DisposeAsync();
                if (ReferenceEquals(ownedServices, serviceProvider)) ownedServices = null;
            },
        };
    });
}
finally
{
    ownedCatalog?.Dispose();
    if (ownedServices is not null) await ownedServices.DisposeAsync();
}

static SunderWorkerProviderRegistration Register(
    string providerId,
    string contractId,
    ISunderRpcServiceHandler handler)
{
    var descriptor = AgentRpcContractDescriptors.Get(contractId);
    return new SunderWorkerProviderRegistration(
        providerId,
        descriptor.ContractId,
        descriptor.Version,
        descriptor.Sha256,
        descriptor.AbiSha256 ?? throw new InvalidOperationException(
            $"Agent RPC contract '{descriptor.ContractId}' does not have a V2 ABI identity."),
        handler);
}

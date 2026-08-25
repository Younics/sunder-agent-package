using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Skills.Protocol;
using Sunder.Package.Agent.Skills.Protocol.Generated;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
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
        services.AddSingleton(_ => new HttpClient(
            new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.AddSingleton<IGitHubSkillClient, GitHubSkillClient>();
        services.AddSingleton<SkillStore>();
        services.AddSingleton<SkillImportService>();
        services.AddSingleton<SkillsFeature>();
        services.AddSingleton<SkillStackContributor>();
        services.AddSingleton<SkillRuntimeHandler>();
        services.AddSingleton<SkillRuntimeChangeStream>();
        services.AddSingleton<SkillOwnerProvider>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        ownedServices = serviceProvider;

        var feature = serviceProvider.GetRequiredService<SkillsFeature>();
        var owner = serviceProvider.GetRequiredService<SkillOwnerProvider>();
        var ownerDescriptor = SkillsOwnerProtocol.Descriptor;
        return new SunderWorkerOptions(
        [
            Register(
                "skills.selectable.capabilities",
                AgentRpcContractIds.SelectableCapabilityProvider,
                AgentSelectableCapabilityProviderRpc.CreateHandler(feature)),
            Register(
                "skills.tools",
                AgentRpcContractIds.ToolSource,
                AgentToolSourceRpc.CreateHandler(feature, catalog)),
            Register(
                "skills.prompt.context",
                AgentRpcContractIds.PromptContextContributor,
                AgentPromptContextContributorRpc.CreateHandler(feature, catalog)),
            new SunderWorkerProviderRegistration(
                "skills.stack",
                SunderStackContributorRpc.ContractId,
                SunderStackContributorRpc.ContractVersion,
                SunderStackContributorRpc.DescriptorSha256,
                SunderStackContributorRpc.AbiSha256,
                SunderStackContributorRpc.CreateHandler(
                    serviceProvider.GetRequiredService<SkillStackContributor>())),
            new SunderWorkerProviderRegistration(
                SkillsOwnerProtocol.ProviderId,
                ownerDescriptor.ContractId,
                ownerDescriptor.Version,
                ownerDescriptor.Sha256,
                ownerDescriptor.AbiSha256 ?? throw new InvalidDataException(
                    "The Skills owner descriptor is missing its V2 ABI identity."),
                new SunderPackageAgentSkillsOwnerRpcHandler(owner)),
        ])
        {
            OnActivated = (_, _) =>
            {
                serviceProvider.GetRequiredService<SkillStore>().Activate();
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

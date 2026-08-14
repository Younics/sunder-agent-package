using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
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
        services.AddSingleton<FilesToolSource>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        ownedServices = serviceProvider;
        var source = serviceProvider.GetRequiredService<FilesToolSource>();
        return new SunderWorkerOptions(
        [
            Register(
                "files.tools",
                AgentRpcContractIds.ToolSource,
                AgentToolSourceRpc.CreateHandler(source, catalog)),
            Register(
                "files.permissions",
                AgentRpcContractIds.PermissionSurface,
                AgentPermissionSurfaceRpc.CreateHandler(source)),
            Register(
                "files.prompt.context",
                AgentRpcContractIds.PromptContextContributor,
                AgentPromptContextContributorRpc.CreateHandler(source, catalog)),
            Register(
                "files.session.cleaner",
                AgentRpcContractIds.SessionCleaner,
                AgentSessionCleanerRpc.CreateHandler(source)),
        ])
        {
            OnActivated = (_, _) =>
            {
                source.Activate();
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

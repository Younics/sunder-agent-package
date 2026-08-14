using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tools.Web;
using Sunder.Package.Agent.Tools.Web.Backends;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

ServiceProvider? ownedServices = null;
try
{
    await SunderWorker.RunAsync(context =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton<WebToolsSettingsService>();
        services.AddSingleton<IWebHostResolver, SystemWebHostResolver>();
        services.AddSingleton<WebUrlNetworkPolicy>();
        services.AddSingleton<IWebFetchHttpClientFactory, PinnedWebFetchHttpClientFactory>();
        services.AddSingleton(provider => new WebFetchService(
            provider.GetRequiredService<WebUrlNetworkPolicy>(),
            provider.GetRequiredService<IWebFetchHttpClientFactory>()));
        services.AddSingleton<ExaWebSearchBackend>();
        services.AddSingleton<WebFetchTool>();
        services.AddSingleton<WebSearchTool>();
        services.AddSingleton(provider => new AgentStaticToolSourceAdapter(
            "web",
            "Web Tools",
            "web",
            [provider.GetRequiredService<WebFetchTool>(), provider.GetRequiredService<WebSearchTool>()]));
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        ownedServices = serviceProvider;
        var source = serviceProvider.GetRequiredService<AgentStaticToolSourceAdapter>();

        return new SunderWorkerOptions(
        [
            Register(
                "web.tools",
                AgentRpcContractIds.ToolSource,
                AgentToolSourceRpc.CreateHandler(source)),
        ])
        {
            SettingsSchema = WebToolsConfiguration.Schema,
            OnShutdown = async (_, _) =>
            {
                await serviceProvider.DisposeAsync();
                if (ReferenceEquals(ownedServices, serviceProvider)) ownedServices = null;
            },
        };
    });
}
finally
{
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

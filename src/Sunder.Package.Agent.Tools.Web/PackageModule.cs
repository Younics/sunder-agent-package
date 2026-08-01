using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Tools.Web.Backends;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Web;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
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
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsSchema(WebToolsConfiguration.Schema);
        registry.RegisterRpcProvider("web.tools", AgentToolSourceRpc.CreateHandler(services.GetRequiredService<AgentStaticToolSourceAdapter>()));
    }
}

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Tools.Web.Backends;
using Sunder.Package.Agent.Tools.Web.Services;
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
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(WebToolsConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.Tools, services.GetRequiredService<WebFetchTool>());
        registry.RegisterExtension(PackageExtensionPoints.Tools, services.GetRequiredService<WebSearchTool>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterExtension(PackageExtensionPoints.Tools, services.GetRequiredService<WebFetchTool>());
        registry.RegisterExtension(PackageExtensionPoints.Tools, services.GetRequiredService<WebSearchTool>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}

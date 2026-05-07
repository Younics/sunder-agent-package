using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Tools.Web.Backends;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Web;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<WebToolsSettingsService>();
        services.AddSingleton<WebFetchService>();
        services.AddSingleton<ExaWebSearchBackend>();
        services.AddSingleton<WebFetchTool>();
        services.AddSingleton<WebSearchTool>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(WebToolsConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.Tools, services.GetRequiredService<WebFetchTool>());
        registry.RegisterExtension(PackageExtensionPoints.Tools, services.GetRequiredService<WebSearchTool>());
    }
}

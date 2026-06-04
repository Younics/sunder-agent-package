using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Mcp;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<McpServerCatalogService>();
        services.AddSingleton(_ => new McpClientConnectionManager(context.LoggerFactory));
        services.AddSingleton<McpToolSource>();
        services.AddSingleton<McpServerStackContributor>();
        services.AddTransient<AgentMcpSettingsViewModel>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(McpPackageConfiguration.Schema);
        registry.RegisterSettingsView<AgentMcpSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, services.GetRequiredService<McpToolSource>());
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, services.GetRequiredService<McpToolSource>());
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, services.GetRequiredService<McpServerStackContributor>());
    }
}

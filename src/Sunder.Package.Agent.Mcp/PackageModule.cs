using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Mcp;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<McpServerCatalogService>();
        services.AddSingleton<McpOAuthService>();
        services.AddSingleton(serviceProvider => new McpClientConnectionManager(
            context.LoggerFactory,
            serviceProvider.GetRequiredService<McpOAuthService>()));
        services.AddSingleton<McpEcosystemConfigurationImporter>();
        services.AddSingleton(serviceProvider => new McpSunderConfigurationSyncService(
            serviceProvider.GetRequiredService<McpEcosystemConfigurationImporter>(),
            serviceProvider.GetService<IPackageExtensionCatalog>()));
        services.AddSingleton<McpConfigurationCoordinator>();
        services.AddSingleton<McpSettingsEditorService>();
        services.AddSingleton<McpServerConnectionService>();
        services.AddSingleton<McpOAuthCoordinator>();
        services.AddSingleton<McpToolSource>();
        services.AddSingleton<McpServerStackContributor>();
        services.AddTransient<AgentMcpSettingsViewModel>();
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        services.GetRequiredService<McpConfigurationCoordinator>().Start();
        registry.RegisterConfigurationSchema(McpPackageConfiguration.Schema);
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, services.GetRequiredService<McpToolSource>());
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, services.GetRequiredService<McpToolSource>());
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, services.GetRequiredService<McpServerStackContributor>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<AgentMcpSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, services.GetRequiredService<McpToolSource>());
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, services.GetRequiredService<McpToolSource>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}

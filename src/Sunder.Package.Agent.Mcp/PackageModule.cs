using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Package.Agent.Shared.Presentation;
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
        services.AddSingleton<McpOAuthCallbackHandler>();
        services.AddSingleton<IPackageCallbackHandler>(provider => provider.GetRequiredService<McpOAuthCallbackHandler>());
        services.AddSingleton(serviceProvider => new McpClientConnectionManager(
            context.Logging.LoggerFactory,
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
        services.AddSingleton<McpRuntimeHandler>();
        services.AddSingleton<McpRuntimeChangeStream>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        services.GetRequiredService<McpConfigurationCoordinator>().Start();
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, services.GetRequiredService<McpToolSource>());
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, services.GetRequiredService<McpToolSource>());
        var stackContributor = services.GetRequiredService<McpServerStackContributor>();
        registry.RegisterExtension(SunderStackExtensionPoints.StackExporters, stackContributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImporters, stackContributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImportAppliedHandlers, stackContributor);
        registry.RegisterRuntimeOperation(McpRuntimeOperations.Query, services.GetRequiredService<McpRuntimeHandler>());
        registry.RegisterRuntimeOperation(McpRuntimeOperations.Command, services.GetRequiredService<McpRuntimeHandler>());
        registry.RegisterRuntimeStream(McpRuntimeOperations.Changes, services.GetRequiredService<McpRuntimeChangeStream>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(provider => new McpAppRuntimeGateway(
            provider.GetRequiredService<Sunder.Sdk.Runtime.IPackageRuntimeClient>(),
            context.Callbacks));
        services.AddSingletonAlias<IMcpManagementGateway, McpAppRuntimeGateway>();
        services.AddTransient(provider => new AgentMcpSettingsViewModel(
            provider.GetRequiredService<IMcpManagementGateway>(),
            PresentationDispatcher.Capture()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<AgentMcpSettingsView>();
}

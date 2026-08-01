using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Protocol;
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
        services.AddSingleton(new McpPackageStorageMigration(context));
        services.AddSingleton(provider => new McpServerCatalogService(
            context,
            provider.GetRequiredService<McpPackageStorageMigration>()));
        services.AddSingleton<McpOAuthService>();
        services.AddSingleton<McpOAuthCallbackHandler>();
        services.AddSingleton<IPackageCallbackHandler>(provider => provider.GetRequiredService<McpOAuthCallbackHandler>());
        services.AddSingleton(serviceProvider => new McpClientConnectionManager(
            context.Logging.LoggerFactory,
            serviceProvider.GetRequiredService<McpOAuthService>()));
        services.AddSingleton<McpEcosystemConfigurationImporter>();
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton(serviceProvider => new McpSunderConfigurationSyncService(
            serviceProvider.GetRequiredService<McpEcosystemConfigurationImporter>(),
            serviceProvider.GetService<AgentRpcCatalog>()));
        services.AddSingleton<McpConfigurationCoordinator>();
        services.AddSingleton<McpSettingsEditorService>();
        services.AddSingleton<McpServerConnectionService>();
        services.AddSingleton<McpOAuthCoordinator>();
        services.AddSingleton<McpToolSource>();
        services.AddSingleton<McpSessionConnectionCleaner>();
        services.AddSingleton<McpServerStackContributor>();
        services.AddSingleton<McpRuntimeHandler>();
        services.AddSingleton<McpRuntimeChangeStream>();
        services.AddSingleton<McpPackageRuntimeStartupService>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterBackgroundService<McpPackageRuntimeStartupService>();
        registry.RegisterRpcProvider("mcp.tools", AgentToolSourceRpc.CreateHandler(services.GetRequiredService<McpToolSource>()));
        registry.RegisterRpcProvider("mcp.selectable.capabilities", AgentSelectableCapabilityProviderRpc.CreateHandler(services.GetRequiredService<McpToolSource>()));
        registry.RegisterRpcProvider("mcp.session.cleaner", AgentSessionCleanerRpc.CreateHandler(services.GetRequiredService<McpSessionConnectionCleaner>()));
        var stackContributor = services.GetRequiredService<McpServerStackContributor>();
        registry.RegisterStackContributor("mcp.stack", stackContributor, services);
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

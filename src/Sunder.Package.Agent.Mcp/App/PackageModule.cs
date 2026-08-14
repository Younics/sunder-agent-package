using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<IMcpOwnerManagementClient>(provider =>
            new McpOwnerManagementRpcClient(provider.GetRequiredService<ISunderRpcClient>()));
        services.AddSingleton<IMcpManagementGateway>(provider => new McpAppRuntimeGateway(
            provider.GetRequiredService<IMcpOwnerManagementClient>(),
            context.Callbacks));
        services.AddTransient(provider => new AgentMcpSettingsViewModel(
            provider.GetRequiredService<IMcpManagementGateway>(),
            PresentationDispatcher.Capture()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<AgentMcpSettingsView>();
}

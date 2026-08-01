using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Shell;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton<ShellToolSource>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        var source = services.GetRequiredService<ShellToolSource>();
        registry.RegisterRpcProvider("shell.tools", AgentToolSourceRpc.CreateHandler(source, services.GetRequiredService<AgentRpcCatalog>()));
        registry.RegisterRpcProvider("shell.permissions", AgentPermissionSurfaceRpc.CreateHandler(source));
        registry.RegisterRpcProvider("shell.prompt.context", AgentPromptContextContributorRpc.CreateHandler(source, services.GetRequiredService<AgentRpcCatalog>()));
    }
}

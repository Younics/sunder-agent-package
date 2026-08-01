using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton(_ => new FilesToolSource(context));
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        var source = services.GetRequiredService<FilesToolSource>();
        registry.RegisterRpcProvider("files.tools", AgentToolSourceRpc.CreateHandler(source, services.GetRequiredService<AgentRpcCatalog>()));
        registry.RegisterRpcProvider("files.permissions", AgentPermissionSurfaceRpc.CreateHandler(source));
        registry.RegisterRpcProvider("files.prompt.context", AgentPromptContextContributorRpc.CreateHandler(source, services.GetRequiredService<AgentRpcCatalog>()));
        registry.RegisterRpcProvider("files.session.cleaner", AgentSessionCleanerRpc.CreateHandler(source));
    }
}

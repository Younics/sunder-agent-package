using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Shell;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<ShellToolSource>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        var source = services.GetRequiredService<ShellToolSource>();
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, source);
        registry.RegisterExtension(PackageExtensionPoints.PermissionSurfaces, source);
    }
}

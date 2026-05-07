using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Shell;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<ShellToolSource>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        var source = services.GetRequiredService<ShellToolSource>();
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, source);
        registry.RegisterExtension(PackageExtensionPoints.PermissionSurfaces, source);
    }
}

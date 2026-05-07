using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<FilesToolSource>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        var source = services.GetRequiredService<FilesToolSource>();
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, source);
        registry.RegisterExtension(PackageExtensionPoints.PermissionSurfaces, source);
        registry.RegisterExtension(PackageExtensionPoints.SystemPromptContributors, source);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Agent.Extension.Minimal;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
        => services.AddSingleton<CurrentUtcTimeTool>();

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterExtension(
            PackageExtensionPoints.Tools,
            services.GetRequiredService<CurrentUtcTimeTool>());
}

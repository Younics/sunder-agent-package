using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<BuilderSetupService>();
        services.AddSingleton<BuilderProjectStore>();
        services.AddSingleton<BuilderViewModel>();
        services.AddTransient<BuilderView>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<BuilderView>(new PackageViewRegistration(
            "sunder.package.agent.builder",
            "Package Builder",
            "Assets/builder-icon.png",
            defaultPlacement: PackageViewPlacement.Middle,
            showInHotbarByDefault: true));
    }
}

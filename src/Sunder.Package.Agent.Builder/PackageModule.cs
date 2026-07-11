using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Builder;

public sealed class PackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<IBuilderUiDispatcher, AvaloniaBuilderUiDispatcher>();
        services.AddSingleton<BuilderPathService>();
        services.AddSingleton<BuilderSetupService>();
        services.AddSingleton<BuilderWorkspaceExecutionService>();
        services.AddSingleton<BuilderProjectStore>();
        services.AddSingleton<IBuilderProjectStore>(services => services.GetRequiredService<BuilderProjectStore>());
        services.AddSingleton<BuilderProjectApplicationService>();
        services.AddSingleton<BuilderProjectPersistence>();
        services.AddSingleton<BuilderOperationQueue>();
        services.AddSingleton<BuilderViewModel>();
        services.AddTransient<BuilderView>();
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<BuilderView>(new PackageViewRegistration(
            "sunder.package.agent.builder",
            "Package Builder",
            "Assets/builder-icon.png",
            defaultPlacement: PackageViewPlacement.Middle,
            showInHotbarByDefault: true));
    }
}

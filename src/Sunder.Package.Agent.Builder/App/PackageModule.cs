using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<IBuilderUiDispatcher, AvaloniaBuilderUiDispatcher>();
        services.AddSingleton<BuilderPathService>();
        services.AddSingleton<BuilderSetupService>();
        services.AddSingleton(provider => new BuilderWorkspaceExecutionService(
            provider.GetRequiredService<ISunderRpcClient>()));
        services.AddSingleton<BuilderProjectStore>();
        services.AddSingletonAlias<IBuilderProjectStore, BuilderProjectStore>();
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

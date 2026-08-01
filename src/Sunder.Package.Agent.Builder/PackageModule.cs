using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Builder;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton<BuilderRuntimeHandler>();
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterRuntimeOperation(
            BuilderRuntimeOperations.Execute,
            services.GetRequiredService<BuilderRuntimeHandler>());
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<IBuilderUiDispatcher, AvaloniaBuilderUiDispatcher>();
        services.AddSingleton<BuilderPathService>();
        services.AddSingleton<BuilderSetupService>();
        services.AddSingleton(provider => new BuilderWorkspaceExecutionService(
            provider.GetRequiredService<IPackageRuntimeClient>()));
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

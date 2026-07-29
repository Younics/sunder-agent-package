using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new LocalPackageStorageMigration(context));
        services.AddSingleton<LocalShellCatalogService>();
        services.AddSingleton(provider => new LocalExecutionWorkspaceConfigService(
            context,
            provider.GetRequiredService<LocalPackageStorageMigration>()));
        services.AddSingleton(provider => new LocalExecutionTarget(
            context,
            provider.GetRequiredService<LocalExecutionWorkspaceConfigService>(),
            provider.GetRequiredService<LocalShellCatalogService>()));
        services.AddSingleton<LocalExecutionWorkspaceEditorContributor>();
        services.AddSingleton<LocalExecutionRuntimeOperationHandler>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterBackgroundService<LocalPackageStorageMigration>();
        registry.RegisterSettingsSchema(LocalExecutionConfiguration.Schema);
        var target = services.GetRequiredService<LocalExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspacePathMigrationContributors, services.GetRequiredService<LocalExecutionWorkspaceConfigService>());
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<LocalExecutionWorkspaceEditorContributor>());
        registry.RegisterRuntimeOperation(LocalExecutionRuntimeOperations.Execute, services.GetRequiredService<LocalExecutionRuntimeOperationHandler>());
    }

}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<LocalExecutionAppRuntimeClient>();
        services.AddSingleton<LocalExecutionWorkspaceEditorPresentationContributor>();
        services.AddTransient(provider => new LocalExecutionSettingsViewModel(
            provider.GetRequiredService<LocalExecutionAppRuntimeClient>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<LocalExecutionSettingsView>();
        registry.RegisterExtension(
            PackageExtensionPoints.WorkspaceEditorContributors,
            services.GetRequiredService<LocalExecutionWorkspaceEditorPresentationContributor>());
    }
}

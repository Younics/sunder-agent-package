using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<LocalShellCatalogService>();
        services.AddSingleton<LocalExecutionWorkspaceConfigService>();
        services.AddSingleton<LocalExecutionTarget>();
        services.AddSingleton<LocalExecutionWorkspaceEditorContributor>();
        services.AddSingleton<LocalExecutionRuntimeOperationHandler>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsSchema(LocalExecutionConfiguration.Schema);
        var target = services.GetRequiredService<LocalExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceBindingContributors, target);
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

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
        services.AddTransient<LocalExecutionSettingsViewModel>();
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LocalExecutionConfiguration.Schema);
        var target = services.GetRequiredService<LocalExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceBindingContributors, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspacePathMigrationContributors, services.GetRequiredService<LocalExecutionWorkspaceConfigService>());
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<LocalExecutionWorkspaceEditorContributor>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<LocalExecutionSettingsView>();
        var target = services.GetRequiredService<LocalExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceBindingContributors, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspacePathMigrationContributors, services.GetRequiredService<LocalExecutionWorkspaceConfigService>());
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<LocalExecutionWorkspaceEditorContributor>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}

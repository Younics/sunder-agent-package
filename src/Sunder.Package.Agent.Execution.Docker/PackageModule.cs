using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new DockerPackageStorageMigration(context));
        services.AddSingleton<DockerCliRunner>();
        services.AddSingleton(provider => new DockerImageCatalogService(
            context,
            provider.GetRequiredService<DockerCliRunner>(),
            provider.GetRequiredService<DockerPackageStorageMigration>()));
        services.AddSingleton<DockerImageStackContributor>();
        services.AddSingleton(provider => new DockerExecutionWorkspaceConfigService(
            context,
            provider.GetRequiredService<DockerImageCatalogService>(),
            provider.GetRequiredService<DockerPackageStorageMigration>()));
        services.AddSingleton<DockerContainerLifecycleService>();
        services.AddSingleton<DockerExecutionTarget>();
        services.AddSingleton<DockerExecutionWorkspaceEditorContributor>();
        services.AddSingleton<DockerExecutionRuntimeOperationHandler>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterBackgroundService<DockerPackageStorageMigration>();
        registry.RegisterSettingsSchema(DockerExecutionConfiguration.Schema);
        var stackContributor = services.GetRequiredService<DockerImageStackContributor>();
        registry.RegisterExtension(SunderStackExtensionPoints.StackExporters, stackContributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImporters, stackContributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImportAppliedHandlers, stackContributor);
        var target = services.GetRequiredService<DockerExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspacePathMigrationContributors, services.GetRequiredService<DockerExecutionWorkspaceConfigService>());
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<DockerExecutionWorkspaceEditorContributor>());
        registry.RegisterRuntimeOperation(DockerExecutionRuntimeOperations.Execute, services.GetRequiredService<DockerExecutionRuntimeOperationHandler>());
    }

}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<DockerExecutionAppRuntimeClient>();
        services.AddSingleton<DockerExecutionWorkspaceEditorPresentationContributor>();
        services.AddTransient(provider => new DockerExecutionSettingsViewModel(
            provider.GetRequiredService<DockerExecutionAppRuntimeClient>(),
            provider.GetRequiredService<IBackgroundProcessQueue>(),
            context.Logging.LoggerFactory.CreateLogger<DockerExecutionSettingsViewModel>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<DockerExecutionSettingsView>();
        registry.RegisterExtension(
            PackageExtensionPoints.WorkspaceEditorContributors,
            services.GetRequiredService<DockerExecutionWorkspaceEditorPresentationContributor>());
    }
}

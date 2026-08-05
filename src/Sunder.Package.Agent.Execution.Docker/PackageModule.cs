using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Runtime;

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
        registry.RegisterStackContributor("docker.stack", stackContributor, services);
        var target = services.GetRequiredService<DockerExecutionTarget>();
        registry.RegisterRpcProvider("docker.execution.target", AgentExecutionTargetRpc.CreateHandler(target));
        registry.RegisterRpcProvider("docker.workspace.path.migrator", AgentWorkspacePathMigratorRpc.CreateHandler(services.GetRequiredService<DockerExecutionWorkspaceConfigService>()));
        registry.RegisterRpcProvider("docker.workspace.editor", AgentWorkspaceEditorRpc.CreateHandler(services.GetRequiredService<DockerExecutionWorkspaceEditorContributor>()));
        registry.RegisterRuntimeOperation(DockerExecutionRuntimeOperations.Execute, services.GetRequiredService<DockerExecutionRuntimeOperationHandler>());
    }

}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient(provider => new DockerExecutionSettingsViewModel(
            provider.GetRequiredService<IPackageRuntimeClient>(),
            provider.GetRequiredService<IBackgroundProcessQueue>(),
            context.Logging.LoggerFactory.CreateLogger<DockerExecutionSettingsViewModel>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsView<DockerExecutionSettingsView>();
    }
}

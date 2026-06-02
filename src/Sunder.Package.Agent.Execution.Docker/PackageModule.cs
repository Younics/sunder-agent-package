using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Shared.Stacks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<DockerCliRunner>();
        services.AddSingleton<DockerImageCatalogService>();
        services.AddSingleton<DockerExecutionWorkspaceConfigService>();
        services.AddSingleton<DockerContainerLifecycleService>();
        services.AddSingleton<DockerExecutionTarget>();
        services.AddSingleton<DockerExecutionWorkspaceEditorContributor>();
        services.AddTransient<DockerExecutionSettingsViewModel>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(DockerExecutionConfiguration.Schema);
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, new PackageConfigurationStackContributor(DockerExecutionConfiguration.Schema, services.GetRequiredService<IPackageContext>()));
        registry.RegisterSettingsView<DockerExecutionSettingsView>();
        var target = services.GetRequiredService<DockerExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceBindingContributors, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspacePathMigrationContributors, services.GetRequiredService<DockerExecutionWorkspaceConfigService>());
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<DockerExecutionWorkspaceEditorContributor>());
    }
}

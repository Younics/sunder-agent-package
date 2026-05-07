using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<DockerExecutionWorkspaceConfigService>();
        services.AddSingleton<DockerContainerLifecycleService>();
        services.AddSingleton<DockerExecutionTarget>();
        services.AddSingleton<DockerExecutionWorkspaceEditorContributor>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(DockerExecutionConfiguration.Schema);
        var target = services.GetRequiredService<DockerExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceBindingContributors, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<DockerExecutionWorkspaceEditorContributor>());
    }
}

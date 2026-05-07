using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<LocalShellCatalogService>();
        services.AddSingleton<LocalExecutionWorkspaceConfigService>();
        services.AddSingleton<LocalExecutionTarget>();
        services.AddSingleton<LocalExecutionWorkspaceEditorContributor>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(LocalExecutionConfiguration.Schema);
        registry.RegisterSettingsView<LocalExecutionSettingsView>();
        var target = services.GetRequiredService<LocalExecutionTarget>();
        registry.RegisterExtension(PackageExtensionPoints.ExecutionTargets, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceBindingContributors, target);
        registry.RegisterExtension(PackageExtensionPoints.WorkspaceEditorContributors, services.GetRequiredService<LocalExecutionWorkspaceEditorContributor>());
    }
}

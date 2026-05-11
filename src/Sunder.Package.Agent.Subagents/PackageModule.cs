using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<SubagentStore>();
        services.AddSingleton<SubagentService>();
        services.AddSingleton<SubagentFeature>();
        services.AddSingleton<OrchestratedAgentBehaviorLoop>();
        services.AddTransient<SubagentsViewModel>();
        services.AddTransient<SubsessionsViewModel>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        var feature = services.GetRequiredService<SubagentFeature>();
        registry.RegisterPackageView<SubagentsView>(new PackageViewRegistration(
            "sunder.package.agent.subagents",
            "Subagents",
            "assets/sub-profile-icon.png",
            defaultPlacement: PackageViewPlacement.LeftTop));
        registry.RegisterPackageView<SubsessionsView>(new PackageViewRegistration(
            SubagentConstants.SubsessionsViewId,
            "Subsessions",
            "assets/sub-session-icon.png",
            defaultPlacement: PackageViewPlacement.LeftTop,
            showInHotbarByDefault: false));
        
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, feature);
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, feature);
        registry.RegisterExtension(PackageExtensionPoints.SystemPromptContributors, feature);
        registry.RegisterExtension(PackageExtensionPoints.BehaviorLoops, services.GetRequiredService<OrchestratedAgentBehaviorLoop>());
    }
}

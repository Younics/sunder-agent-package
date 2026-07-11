using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Subagents;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<SubagentStore>();
        services.AddSingleton<SubagentService>();
        services.AddSingleton<SubagentFeature>();
        services.AddSingleton<OrchestratedAgentBehaviorLoop>();
        services.AddSingleton<SubagentStackContributor>();
        services.AddTransient<SubagentsViewModel>();
        services.AddTransient<SubsessionsViewModel>();
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services
    )
    {
        var feature = services.GetRequiredService<SubagentFeature>();

        registry.RegisterExtension(
            PackageExtensionPoints.ProfileSelectableCapabilityProviders,
            feature
        );
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, feature);
        registry.RegisterExtension(PackageExtensionPoints.SystemPromptContributors, feature);
        registry.RegisterExtension(
            PackageExtensionPoints.BehaviorLoops,
            services.GetRequiredService<OrchestratedAgentBehaviorLoop>()
        );
        registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, services.GetRequiredService<SubagentStackContributor>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        var feature = services.GetRequiredService<SubagentFeature>();
        registry.RegisterPackageView<SubsessionsView>(new PackageViewRegistration(
            SubagentConstants.SubsessionsViewId,
            "Subsessions",
            "assets/sub-session-icon.png",
            defaultPlacement: PackageViewPlacement.LeftTop));
        registry.RegisterPackageView<SubagentsView>(new PackageViewRegistration(
            "sunder.package.agent.subagents",
            "Subagents",
            "assets/sub-profile-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, feature);
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, feature);
        registry.RegisterExtension(PackageExtensionPoints.SystemPromptContributors, feature);
        registry.RegisterExtension(PackageExtensionPoints.BehaviorLoops, services.GetRequiredService<OrchestratedAgentBehaviorLoop>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}

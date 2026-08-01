using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Subagents;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<SubagentStore>();
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton<SubagentService>();
        services.AddSingleton<SubagentFeature>();
        services.AddSingleton<OrchestratedAgentBehaviorLoop>();
        services.AddSingleton<SubagentStackContributor>();
        services.AddSingleton<SubagentRuntimeHandler>();
        services.AddSingleton<SubagentRuntimeChangeStream>();
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services
    )
    {
        var feature = services.GetRequiredService<SubagentFeature>();

        registry.RegisterRpcProvider("subagents.selectable.capabilities", AgentSelectableCapabilityProviderRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("subagents.tools", AgentToolSourceRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("subagents.prompt.context", AgentPromptContextContributorRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("subagents.behavior.loop", AgentBehaviorLoopRpc.CreateHandler(
            services.GetRequiredService<OrchestratedAgentBehaviorLoop>(),
            services.GetRequiredService<AgentRpcCatalog>()));
        var stackContributor = services.GetRequiredService<SubagentStackContributor>();
        registry.RegisterStackContributor("subagents.stack", stackContributor, services);
        registry.RegisterRuntimeOperation(SubagentRuntimeOperations.Query, services.GetRequiredService<SubagentRuntimeHandler>());
        registry.RegisterRuntimeOperation(SubagentRuntimeOperations.Command, services.GetRequiredService<SubagentRuntimeHandler>());
        registry.RegisterRuntimeStream(SubagentRuntimeOperations.Changes, services.GetRequiredService<SubagentRuntimeChangeStream>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubagentManagementGateway, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionSessionReader, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionCheckpointReader, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionTranscriptPageReader, SubagentAppRuntimeGateway>();
        services.AddSingletonAlias<ISubsessionChangeNotifications, SubagentAppRuntimeGateway>();
        services.AddTransient(provider => new SubagentsViewModel(
            provider.GetRequiredService<ISubagentManagementGateway>(),
            provider.GetService<IPackageSettingsNavigationService>()));
        services.AddTransient(provider => new SubsessionsViewModel(
            provider.GetRequiredService<ISubsessionSessionReader>(),
            provider.GetRequiredService<ISubsessionCheckpointReader>(),
            provider.GetRequiredService<ISubsessionTranscriptPageReader>(),
            provider.GetRequiredService<ISubsessionChangeNotifications>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
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
    }
}

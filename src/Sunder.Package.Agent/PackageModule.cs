using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent;

public sealed partial class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new AgentLocalStore(context));
        services.AddSingleton<AgentWorkspaceService>();
        services.AddSingleton<AgentExecutionTargetService>();
        services.AddSingleton<AgentExecutionTargetWarmupService>();
        services.AddSingleton<AgentProfileService>();
        services.AddSingleton<AgentSessionService>();
        services.AddSingleton<AgentAttachmentService>();
        services.AddSingleton<AgentRunAttachmentStore>();
        services.AddSingleton<IAgentAttachmentContentStore>(provider => provider.GetRequiredService<AgentAttachmentService>());
        services.AddSingleton<AgentRuntimeCatalog>();
        services.AddSingleton<AgentChatSelectionStateService>();
        services.AddSingleton<InstalledPackageToolSource>();
        services.AddSingleton<AgentToolPresentationService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<AgentPermissionService>();
        services.AddSingleton<AgentMemoryCoordinator>();
        services.AddSingleton<AgentSystemPromptComposer>();
        services.AddSingleton<DefaultAgentBehaviorLoop>();
        services.AddSingleton<AgentBehaviorLoopResolver>();
        services.AddSingleton<AgentBehaviorLoopHostFactory>();
        services.AddSingleton<AgentActiveRunRegistry>();
        services.AddSingleton<AgentRunEventLogger>();
        services.AddSingleton<AgentRunProviderResolver>();
        services.AddSingleton<AgentRunStopCoordinator>();
        services.AddSingleton<AgentChildRunSessionService>();
        services.AddSingleton<AgentParentRunContinuationService>();
        services.AddSingleton<AgentPermissionResumeCoordinator>();
        services.AddSingleton<AgentUserMessageRunCoordinator>();
        services.AddSingleton<AgentRunCoordinator>();
        services.AddSingleton<IAgentChildRunExecutor>(provider => provider.GetRequiredService<AgentRunCoordinator>());
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterExtension(PackageExtensionPoints.RuntimeCatalogs, services.GetRequiredService<AgentRuntimeCatalog>());
        registry.RegisterExtension(PackageExtensionPoints.ChildRunExecutors, services.GetRequiredService<AgentRunCoordinator>());
        registry.RegisterExtension(PackageExtensionPoints.AttachmentContentStores, services.GetRequiredService<AgentAttachmentService>());
        registry.RegisterExtension(PackageExtensionPoints.SessionDataCleaners, services.GetRequiredService<AgentAttachmentService>());
        registry.RegisterExtension(PackageExtensionPoints.BehaviorLoops, services.GetRequiredService<DefaultAgentBehaviorLoop>());
        registry.RegisterPackageView<AgentChatView>(new PackageViewRegistration(
            "sunder.package.agent.chat",
            "Agent Chat",
            "Assets/chat-icon.png",
            defaultPlacement: PackageViewPlacement.Middle));
        registry.RegisterPackageView<AgentSessionsView>(new PackageViewRegistration(
            "sunder.package.agent.sessions",
            "Sessions",
            "Assets/session-icon.png",
            defaultPlacement: PackageViewPlacement.LeftTop));
        registry.RegisterPackageView<AgentProfilesView>(new PackageViewRegistration(
            "sunder.package.agent.profiles",
            "Agent Profiles",
            "Assets/profile-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterPackageView<AgentWorkspacesView>(new PackageViewRegistration(
            "sunder.package.agent.workspaces",
            "Workspaces",
            "Assets/workspace-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterSettingsView<AgentPermissionsView>();
    }
}

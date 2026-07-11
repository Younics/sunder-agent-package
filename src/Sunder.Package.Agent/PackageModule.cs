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
        services.AddSingleton<AgentExecutionTargetService>();
        services.AddSingleton<AgentWorkspaceExecutionResolver>();
        services.AddSingleton<AgentExecutionTargetWarmupService>();
        services.AddSingleton<AgentProfileService>();
        services.AddSingleton<AgentProfileStackContributor>();
        services.AddSingleton<AgentSessionService>();
        services.AddSingleton(provider => new AgentWorkspaceService(
            provider.GetRequiredService<AgentLocalStore>(),
            provider.GetService<IPackageExtensionCatalog>(),
            provider.GetRequiredService<AgentSessionService>()
        ));
        services.AddSingleton<AgentWorkspaceStackContributor>();
        services.AddSingleton<AgentAttachmentService>();
        services.AddSingleton<AgentRunAttachmentStore>();
        services.AddSingleton<IAgentAttachmentContentStore>(provider =>
            provider.GetRequiredService<AgentAttachmentService>()
        );
        services.AddSingleton<AgentRuntimeCatalog>();
        services.AddSingleton<AgentChatSelectionStateService>();
        services.AddSingleton<InstalledPackageToolSource>();
        services.AddSingleton<AgentToolPresentationService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<AgentPermissionService>();
        services.AddSingleton<AgentMemoryCoordinator>();
        services.AddSingleton<AgentSessionContextProjectionService>();
        services.AddSingleton<AgentSystemPromptComposer>();
        services.AddSingleton<WorkspaceDocumentationContextService>();
        services.AddSingleton<AgentLoopTerminalHandler>();
        services.AddSingleton<AgentStreamingTurnWriter>();
        services.AddSingleton<AgentPromptPreparationPipeline>(provider => new AgentPromptPreparationPipeline(
            provider.GetRequiredService<AgentSystemPromptComposer>(),
            provider.GetService<IAgentAttachmentContentStore>(),
            provider.GetRequiredService<AgentSessionContextProjectionService>()
        ));
        services.AddSingleton<AgentProviderCycleRunner>();
        services.AddSingleton<AgentToolCycleCoordinator>();
        services.AddSingleton(provider => new DefaultAgentBehaviorLoop(
            provider.GetRequiredService<AgentPromptPreparationPipeline>(),
            provider.GetRequiredService<AgentProviderCycleRunner>(),
            provider.GetRequiredService<AgentToolCycleCoordinator>(),
            provider.GetRequiredService<AgentLoopTerminalHandler>()
        ));
        services.AddSingleton<AgentBehaviorLoopResolver>();
        services.AddSingleton<AgentBehaviorLoopHostFactory>();
        services.AddSingleton<AgentActiveRunRegistry>();
        services.AddSingleton<AgentSessionTransitionGate>();
        services.AddSingleton<AgentRunEventLogger>();
        services.AddSingleton<AgentRunProviderResolver>();
        services.AddSingleton<AgentSessionTitleService>();
        services.AddSingleton<AgentRunPreparationService>();
        services.AddSingleton<AgentRunStartService>();
        services.AddSingleton<AgentRunExecutionService>();
        services.AddSingleton<AgentRunStopCoordinator>();
        services.AddSingleton<AgentChildRunSessionService>();
        services.AddSingleton<AgentParentRunContinuationService>();
        services.AddSingleton<AgentPermissionResumeCoordinator>();
        services.AddSingleton(provider => new AgentUserMessageRunCoordinator(
            provider.GetRequiredService<AgentSessionService>(),
            provider.GetRequiredService<AgentRunPreparationService>(),
            provider.GetRequiredService<AgentRunStartService>(),
            provider.GetRequiredService<AgentRunExecutionService>(),
            provider.GetRequiredService<AgentActiveRunRegistry>(),
            provider.GetRequiredService<AgentSessionTransitionGate>()
        ));
        services.AddSingleton<AgentRunCoordinator>();
        services.AddSingleton<IAgentChildRunExecutor>(provider =>
            provider.GetRequiredService<AgentRunCoordinator>()
        );
    }

    public void RegisterContributions(
        IPackageContributionRegistry registry,
        IServiceProvider services
    )
    {
        services.GetRequiredService<AgentParentRunContinuationService>().StartRecovery();
        registry.RegisterExtension(
            PackageExtensionPoints.RuntimeCatalogs,
            services.GetRequiredService<AgentRuntimeCatalog>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.WorkspaceExecutionResolvers,
            services.GetRequiredService<AgentWorkspaceExecutionResolver>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.ChildRunExecutors,
            services.GetRequiredService<AgentRunCoordinator>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.AttachmentContentStores,
            services.GetRequiredService<AgentAttachmentService>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.SessionDataCleaners,
            services.GetRequiredService<AgentAttachmentService>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.BehaviorLoops,
            services.GetRequiredService<DefaultAgentBehaviorLoop>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.SystemPromptContributors,
            services.GetRequiredService<WorkspaceDocumentationContextService>()
        );
        registry.RegisterExtension(
            Sunder.Sdk.Stacks.SunderStackExtensionPoints.StackContributors,
            services.GetRequiredService<AgentProfileStackContributor>()
        );
        registry.RegisterExtension(
            Sunder.Sdk.Stacks.SunderStackExtensionPoints.StackContributors,
            services.GetRequiredService<AgentWorkspaceStackContributor>()
        );

        // Middle
        registry.RegisterPackageView<AgentChatView>(
            new PackageViewRegistration(
                "sunder.package.agent.chat",
                "Agent Chat",
                "Assets/chat-icon.png",
                defaultPlacement: PackageViewPlacement.Middle
            )
        );

        // Right-Top
        registry.RegisterPackageView<AgentWorkspacesView>(
            new PackageViewRegistration(
                "sunder.package.agent.workspaces",
                "Workspaces",
                "Assets/workspace-icon.png",
                defaultPlacement: PackageViewPlacement.RightTop
            )
        );
        registry.RegisterPackageView<AgentProfilesView>(
            new PackageViewRegistration(
                "sunder.package.agent.profiles",
                "Agents",
                "Assets/profile-icon.png",
                defaultPlacement: PackageViewPlacement.RightTop
            )
        );

        registry.RegisterSettingsView<AgentPermissionsView>();
    }
}

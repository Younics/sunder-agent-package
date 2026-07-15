using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent;

public sealed partial class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
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
        services.AddSingletonAlias<IAgentAttachmentContentStore, AgentAttachmentService>();
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
        services.AddSingletonAlias<IAgentChildRunExecutor, AgentRunCoordinator>();
        services.AddSingleton<AgentRuntimeChangeHub>();
        services.AddSingleton<AgentChatSnapshotHandler>();
        services.AddSingleton<AgentDashboardHandler>();
        services.AddSingleton<AgentTranscriptPageHandler>();
        services.AddSingleton<AgentCatalogHandler>();
        services.AddSingleton<AgentProfileCommandHandler>();
        services.AddSingleton<AgentWorkspaceCommandHandler>();
        services.AddSingleton<AgentSessionCommandHandler>();
        services.AddSingleton<AgentRunCommandHandler>();
        services.AddSingleton<AgentPermissionCommandHandler>();
        services.AddSingleton<AgentAttachmentReadHandler>();
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
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
        RegisterStackContributor(registry, services.GetRequiredService<AgentProfileStackContributor>());
        RegisterStackContributor(registry, services.GetRequiredService<AgentWorkspaceStackContributor>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.ChatSnapshot, services.GetRequiredService<AgentChatSnapshotHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Dashboard, services.GetRequiredService<AgentDashboardHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Transcript, services.GetRequiredService<AgentTranscriptPageHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Catalog, services.GetRequiredService<AgentCatalogHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Profiles, services.GetRequiredService<AgentProfileCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Workspaces, services.GetRequiredService<AgentWorkspaceCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.SessionCommands, services.GetRequiredService<AgentSessionCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Runs, services.GetRequiredService<AgentRunCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Permissions, services.GetRequiredService<AgentPermissionCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Attachments, services.GetRequiredService<AgentAttachmentReadHandler>());
        registry.RegisterRuntimeStream(AgentRuntimeOperations.Changes, services.GetRequiredService<AgentRuntimeChangeHub>());
    }

    private static void RegisterStackContributor<TContributor>(
        ISunderRuntimeContributionRegistry registry,
        TContributor contributor)
        where TContributor : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
    {
        registry.RegisterExtension(SunderStackExtensionPoints.StackExporters, contributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImporters, contributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImportAppliedHandlers, contributor);
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentRuntimeAvailability, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentProfileGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentWorkspaceGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentSessionGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentPermissionGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentRunGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentAttachmentGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentExecutionGateway, AgentAppRuntimeGateway>();
        services.AddSingleton<AgentChatSelectionStateService>();
        services.AddSingleton<AgentToolPresentationService>();
        services.AddTransient<AgentWorkspacesViewContext>();
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<AgentChatView>(new PackageViewRegistration(
            "sunder.package.agent.chat", "Agent Chat", "Assets/chat-icon.png",
            defaultPlacement: PackageViewPlacement.Middle));
        registry.RegisterPackageView<AgentWorkspacesView>(new PackageViewRegistration(
            "sunder.package.agent.workspaces", "Workspaces", "Assets/workspace-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterPackageView<AgentProfilesView>(new PackageViewRegistration(
            "sunder.package.agent.profiles", "Agents", "Assets/profile-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterSettingsView<AgentPermissionsView>();
    }
}

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent;

public sealed partial class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new AgentPackageStorageMigration(context));
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
        services.AddSingleton<AgentAttachmentTransferService>();
        services.AddSingleton<AgentRunAttachmentStore>();
        services.AddSingleton<AgentRuntimeCatalog>();
        services.AddSingleton(provider => new AgentChatSelectionStateService(
            context,
            provider.GetRequiredService<AgentPackageStorageMigration>()));
        services.AddSingleton<InstalledPackageToolSource>();
        services.AddSingleton<AgentToolPresentationService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<AgentPermissionService>();
        services.AddSingleton(provider => new AgentLifecycleDispatcher(
            provider.GetRequiredService<AgentLocalStore>(),
            provider.GetRequiredService<IPackageExtensionCatalog>(),
            context.Logging.Events));
        services.AddSingleton<AgentSessionCleanupDispatcher>();
        services.AddSingleton<AgentMemoryCoordinator>();
        services.AddSingleton<AgentSessionContinuityGenerationService>();
        services.AddSingleton(provider => new AgentSessionContextProjectionService(
            provider.GetRequiredService<AgentSessionService>(),
            provider.GetRequiredService<AgentSessionContinuityGenerationService>()));
        services.AddSingleton<AgentSystemPromptComposer>();
        services.AddSingleton<WorkspaceDocumentationContextService>();
        services.AddSingleton<AgentLoopTerminalHandler>();
        services.AddSingleton<AgentStreamingTurnWriter>();
        services.AddSingleton<AgentPromptPreparationPipeline>(provider => new AgentPromptPreparationPipeline(
            provider.GetRequiredService<AgentSystemPromptComposer>(),
            provider.GetRequiredService<AgentAttachmentService>(),
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
        services.AddSingleton<AgentSessionDeletionFence>();
        services.AddSingleton<AgentSessionDeletionService>();
        services.AddSingleton<AgentBackgroundWorkService>();
        services.AddSingleton<AgentRunEventLogger>();
        services.AddSingleton<AgentRunProviderResolver>();
        services.AddSingleton<AgentSessionTitleService>();
        services.AddSingleton<AgentRunPreparationService>();
        services.AddSingleton<AgentRunStartService>();
        services.AddSingleton<AgentRunExecutionService>();
        services.AddSingleton<AgentUserTurnAdmissionService>();
        services.AddSingleton<AgentRunDispatcher>();
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
            provider.GetRequiredService<AgentSessionTransitionGate>(),
            provider.GetRequiredService<AgentSessionDeletionFence>(),
            provider.GetRequiredService<AgentUserTurnAdmissionService>(),
            provider.GetRequiredService<AgentRunDispatcher>()
        ));
        services.AddSingleton<AgentRunCoordinator>();
        services.AddSingleton<AgentRuntimeChangeHub>();
        services.AddSingleton(provider => new HistorySearchStore(context));
        services.AddSingleton(provider => new HistorySearchRuntimeState(
            provider.GetRequiredService<HistorySearchStore>(),
            provider.GetRequiredService<AgentRuntimeChangeHub>()));
        services.AddSingleton<HistoryEmbeddingProviderCatalog>();
        services.AddSingleton<HistorySemanticOperationFence>();
        services.AddSingleton(provider => new HistorySearchIndexingService(
            provider.GetRequiredService<HistorySearchStore>(),
            provider.GetRequiredService<AgentLocalStore>(),
            provider.GetRequiredService<AgentSessionService>(),
            provider.GetRequiredService<AgentWorkspaceService>(),
            provider.GetRequiredService<HistoryEmbeddingProviderCatalog>(),
            provider.GetRequiredService<HistorySemanticOperationFence>(),
            provider.GetRequiredService<HistorySearchRuntimeState>()));
        services.AddSingleton<HistorySearchService>();
        services.AddSingleton<AgentRuntimeGenerationOptions>();
        services.AddSingleton<AgentRuntimeStartupService>();
        services.AddSingletonAlias<IAgentChildRunExecutor, AgentRunCoordinator>();
        services.AddSingleton<AgentChatSnapshotHandler>();
        services.AddSingleton<AgentDashboardHandler>();
        services.AddSingleton<AgentTranscriptPageHandler>();
        services.AddSingleton<AgentCatalogHandler>();
        services.AddSingleton<AgentProfileCommandHandler>();
        services.AddSingleton<AgentWorkspaceCommandHandler>();
        services.AddSingleton<AgentSessionCommandHandler>();
        services.AddSingleton<AgentRunCommandHandler>();
        services.AddSingleton<AgentPermissionCommandHandler>();
        services.AddSingleton<AgentAttachmentTransferHandler>();
        services.AddSingleton<AgentHistorySearchHandler>();
        services.AddSingleton<AgentHistoryStateHandler>();
        services.AddSingleton<AgentHistoryCommandHandler>();
        services.AddSingleton<AgentTranscriptAroundTurnHandler>();
        services.AddSingleton<AgentTranscriptToolDetailHandler>();
        services.AddSingleton(provider => new AgentHistoryStatusStream(
            provider.GetRequiredService<HistorySearchRuntimeState>()));
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services
    )
    {
        registry.RegisterBackgroundService<AgentPackageStorageMigration>();
        registry.RegisterBackgroundService<AgentRuntimeStartupService>();
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
            PackageExtensionPoints.SessionDataCleaners,
            services.GetRequiredService<AgentAttachmentService>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.SessionDataCleaners,
            services.GetRequiredService<HistorySearchIndexingService>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.BehaviorLoops,
            services.GetRequiredService<DefaultAgentBehaviorLoop>()
        );
        registry.RegisterExtension(
            PackageExtensionPoints.PromptContextContributors,
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
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.RunStatus, services.GetRequiredService<AgentRunCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.Permissions, services.GetRequiredService<AgentPermissionCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.AttachmentTransfers, services.GetRequiredService<AgentAttachmentTransferHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.HistorySearch, services.GetRequiredService<AgentHistorySearchHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.HistoryState, services.GetRequiredService<AgentHistoryStateHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.HistoryCommands, services.GetRequiredService<AgentHistoryCommandHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.TranscriptAround, services.GetRequiredService<AgentTranscriptAroundTurnHandler>());
        registry.RegisterRuntimeOperation(AgentRuntimeOperations.TranscriptToolDetail, services.GetRequiredService<AgentTranscriptToolDetailHandler>());
        registry.RegisterRuntimeStream(AgentRuntimeOperations.Changes, services.GetRequiredService<AgentRuntimeChangeHub>());
        registry.RegisterRuntimeStream(AgentRuntimeOperations.HistoryStatus, services.GetRequiredService<AgentHistoryStatusStream>());
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
        services.AddSingletonAlias<IAgentHistorySearchGateway, AgentAppRuntimeGateway>();
        services.AddSingletonAlias<IAgentTranscriptAnchorGateway, AgentAppRuntimeGateway>();
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
        registry.RegisterPackageView<AgentHistorySearchView>(new PackageViewRegistration(
            "sunder.package.agent.history", "History", "Assets/session-icon.png",
            defaultPlacement: PackageViewPlacement.RightTop));
        registry.RegisterSettingsView<AgentPermissionsView>();
    }
}

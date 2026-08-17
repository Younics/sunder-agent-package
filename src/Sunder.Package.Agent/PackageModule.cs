using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent;

// Retained as a package-local composition entry point for tests and the Worker executable.
public sealed partial class PackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<IPackageContext>(context);
        services.AddSingleton(_ => new AgentLocalStore(context));
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton(provider => new AgentRuntimeTransportBudgetState(
            provider.GetService<ISunderRpcClient>()));
        services.AddSingleton<AgentRunControlRegistry>();
        services.AddSingleton(provider => AgentRpcServices.CreateBehaviorLoops(
            provider.GetRequiredService<AgentRunControlRegistry>(),
            provider.GetRequiredService<AgentRpcCatalog>()));
        services.AddSingleton<AgentExecutionTargetService>();
        services.AddSingleton<AgentWorkspaceExecutionResolver>();
        services.AddSingleton<AgentExecutionTargetWarmupService>();
        services.AddSingleton(provider => new AgentProfileService(
            provider.GetRequiredService<AgentLocalStore>(),
            provider.GetRequiredService<AgentToolService>(),
            provider.GetRequiredService<AgentRpcCatalog>(),
            provider.GetRequiredService<AgentRpcProviderService<IAgentBehaviorLoop>>(),
            refreshProviderSubscriptions: false));
        services.AddSingleton<AgentProfileStackContributor>();
        services.AddSingleton<AgentSessionService>();
        services.AddSingleton(provider => new AgentWorkspaceService(
            provider.GetRequiredService<AgentLocalStore>(),
            provider.GetService<AgentRpcCatalog>(),
            provider.GetRequiredService<AgentSessionService>()));
        services.AddSingleton<AgentWorkspaceStackContributor>();
        services.AddSingleton<AgentAttachmentService>();
        services.AddSingleton<AgentAttachmentTransferService>();
        services.AddSingleton<AgentRunAttachmentStore>();
        services.AddSingleton<AgentRuntimeCatalog>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<AgentPermissionService>();
        services.AddSingleton(provider => new AgentLifecycleDispatcher(
            provider.GetRequiredService<AgentLocalStore>(),
            provider.GetRequiredService<AgentRpcCatalog>(),
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
            provider.GetRequiredService<AgentSessionContextProjectionService>()));
        services.AddSingleton<AgentProviderCycleRunner>();
        services.AddSingleton<AgentToolCycleCoordinator>();
        services.AddSingleton(provider => new DefaultAgentBehaviorLoop(
            provider.GetRequiredService<AgentPromptPreparationPipeline>(),
            provider.GetRequiredService<AgentProviderCycleRunner>(),
            provider.GetRequiredService<AgentToolCycleCoordinator>(),
            provider.GetRequiredService<AgentLoopTerminalHandler>()));
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
            provider.GetRequiredService<AgentRunDispatcher>()));
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
        services.AddSingleton<AgentOwnerRuntimeProvider>();
    }
}

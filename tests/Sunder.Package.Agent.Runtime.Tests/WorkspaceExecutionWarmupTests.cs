using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceExecutionWarmupTests
{
    [Fact]
    public async Task AgentExecutionTargetWarmupService_CallsPrimaryExecutionTargetReadiness()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("test-target");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var warmupService = new AgentExecutionTargetWarmupService(workspaceService, executionTargetService);
        var workspace = workspaceService.CreateWorkspace("Warmup Workspace");
        workspaceService.SavePrimaryExecutionBinding(workspace.WorkspaceId, target.Descriptor.TargetId);

        var result = await warmupService.WarmWorkspaceAsync(workspace);

        Assert.Equal(AgentExecutionTargetWarmupStatus.Ready, result.Status);
        Assert.Equal(1, target.ReadinessCallCount);
        Assert.Null(target.LastContext?.ProfileId);
    }

    [Fact]
    public async Task AgentChatViewModel_WarmsExecutionTarget_WhenWorkspaceSelectionChanges()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var target = new CountingExecutionTarget("test-target");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog.ToolSources, catalog.ObservedToolExecutions);
        var profileService = catalog.CreateProfileService(store, toolService);
        var permissionService = new AgentPermissionService(store, catalog.PermissionSurfaces);
        await using var lifecycle = new AgentLifecycleDispatcher(store, catalog.DurableLifecycleObservers);
        var memoryCoordinator = new AgentMemoryCoordinator(sessionService, catalog.PromptContextContributors, lifecycle);
        var promptComposer = new AgentSystemPromptComposer(catalog.SystemPromptContributors);
        var attachmentService = new AgentAttachmentService(scope.Context);
        var behaviorLoop = new DefaultAgentBehaviorLoop(promptComposer, attachmentService);
        var runAttachmentStore = new AgentRunAttachmentStore(attachmentService);
        var activeRunRegistry = catalog.ActiveRuns;
        var runEventLogger = new AgentRunEventLogger(scope.Context);
        var providerResolver = new AgentRunProviderResolver(profileService, catalog.ChatProviders);
        var behaviorLoopResolver = new AgentBehaviorLoopResolver(
            catalog.BehaviorLoops,
            catalog.RunControls,
            activeRunRegistry,
            behaviorLoop);
        var stopCoordinator = new AgentRunStopCoordinator(sessionService, permissionService, memoryCoordinator, activeRunRegistry, profileService);
        var behaviorLoopHostFactory = new AgentBehaviorLoopHostFactory(sessionService, toolService, permissionService, memoryCoordinator, runEventLogger, activeRunRegistry, behaviorLoop);
        var runPreparationService = new AgentRunPreparationService(sessionService, profileService, workspaceService, runAttachmentStore, runEventLogger, providerResolver);
        var runStartService = new AgentRunStartService(
            sessionService,
            activeRunRegistry,
            runEventLogger);
        var runExecutionService = new AgentRunExecutionService(sessionService, workspaceService, memoryCoordinator, activeRunRegistry, runEventLogger, behaviorLoopHostFactory, behaviorLoopResolver);
        var childRunSessionService = new AgentChildRunSessionService(sessionService, profileService);
        var parentRunContinuationService = new AgentParentRunContinuationService(sessionService, profileService, workspaceService, providerResolver, activeRunRegistry, behaviorLoopHostFactory, behaviorLoopResolver, childRunSessionService);
        var permissionResumeCoordinator = new AgentPermissionResumeCoordinator(sessionService, workspaceService, profileService, permissionService, providerResolver, activeRunRegistry, runEventLogger, behaviorLoopHostFactory, behaviorLoopResolver, parentRunContinuationService);
        var userMessageRunCoordinator = new AgentUserMessageRunCoordinator(
            sessionService,
            runPreparationService,
            runStartService,
            runExecutionService,
            activeRunRegistry);
        var runCoordinator = new AgentRunCoordinator(userMessageRunCoordinator, stopCoordinator,
            new AgentChildRunExecutor(userMessageRunCoordinator, childRunSessionService), permissionResumeCoordinator);
        var warmupService = new AgentExecutionTargetWarmupService(workspaceService, executionTargetService);
        var firstWorkspace = workspaceService.CreateWorkspace("First Workspace");
        workspaceService.SavePrimaryExecutionBinding(firstWorkspace.WorkspaceId, target.Descriptor.TargetId);
        var secondWorkspace = workspaceService.CreateWorkspace("Second Workspace");
        workspaceService.SavePrimaryExecutionBinding(secondWorkspace.WorkspaceId, target.Descriptor.TargetId);

        using var viewModel = RegressionTestAppViews.Chat(
            profileService,
            workspaceService,
            sessionService,
            permissionService,
            runCoordinator,
            warmupService: warmupService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => target.ReadinessCallCount >= 1);

        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace => workspace.WorkspaceId == secondWorkspace.WorkspaceId);

        await WaitUntilAsync(() => target.ReadinessCallCount >= 2
                                  && string.Equals(target.LastContext?.Workspace.WorkspaceId, secondWorkspace.WorkspaceId, StringComparison.OrdinalIgnoreCase));
    }
}

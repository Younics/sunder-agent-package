using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPermissionHardeningTests
{
    [Fact]
    public void AgentToolService_DoesNotExposeRawExecutionPublicly()
    {
        Assert.DoesNotContain(
            typeof(AgentToolService).GetMethods(),
            method => method.IsPublic
                      && string.Equals(method.Name, "ExecuteAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentApprovals_ExecuteMutatingToolExactlyOnce()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        source.BlockExecution();

        var firstApproval = runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);
        await source.ExecutionStarted;
        var secondApproval = runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);
        source.ReleaseExecution();
        await Task.WhenAll(firstApproval, secondApproval);

        Assert.Equal(1, source.ExecutionCount);
        Assert.Equal(
            AgentPendingPermissionStatus.Executed,
            runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
    }

    [Fact]
    public async Task ApprovedExecutionFailure_PersistsFailedTerminalState()
    {
        var source = new PermissionAwareMutationToolSource("mutate") { ThrowOnExecution = true };
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();

        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(1, source.ExecutionCount);
        Assert.Equal(AgentPendingPermissionStatus.Failed, persisted?.Status);
        Assert.NotNull(persisted?.DecidedAtUtc);
        Assert.Contains("injected", persisted?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
    }

    [Fact]
    public async Task PermissionRevalidationException_FinalizesClaimAndRun()
    {
        var source = new PermissionAwareMutationToolSource("mutate")
        {
            ThrowOnPermissionRevalidation = true,
        };
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();

        var checkpoint = await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId);

        Assert.Equal(0, source.ExecutionCount);
        Assert.Equal(AgentRunStatus.Failed, checkpoint?.Status);
        Assert.Equal(AgentDurableRunStatus.Failed, runtime.Store.GetRun(pending.RunId)?.Status);
        Assert.Equal(
            AgentPendingPermissionStatus.Failed,
            runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
    }

    [Fact]
    public async Task RepeatedDenial_IsIdempotentAndPersistsOneToolResult()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();

        await runtime.ResumeCoordinator.DenyAsync(runtime.Session.SessionId, pending.RequestId);
        await runtime.ResumeCoordinator.DenyAsync(runtime.Session.SessionId, pending.RequestId);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(AgentPendingPermissionStatus.Denied, persisted?.Status);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Single(
            runtime.SessionService.ListTurns(runtime.Session.SessionId)
                .SelectMany(turn => turn.Items),
            item => string.Equals(item.ErrorCode, "permission-denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedExecutionBinding_ExpiresApprovalWithoutExecution()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        runtime.WorkspaceService.SavePrimaryExecutionBinding(
            runtime.Workspace.WorkspaceId,
            PermissionHardeningExecutionTarget.SecondaryTargetId);

        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(AgentPendingPermissionStatus.Expired, persisted?.Status);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Contains("context changed", persisted?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnassignedProviderRequestedTool_IsDeniedWithoutSourceResolutionOrExecution()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source, assignTool: false);

        var outcome = await runtime.Host.InvokeToolAsync(
            new AgentToolCallRequest("call-1", source.ToolId, "{}"),
            assistantTurn: null);

        Assert.Equal(AgentToolCallOutcomeKind.Denied, outcome.Kind);
        Assert.Equal(AgentToolSecurityErrorCodes.NotAdvertised, outcome.Result?.ErrorCode);
        Assert.Equal(0, source.PermissionResolutionCount);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
    }

    [Fact]
    public async Task MutatingToolWithoutPermissionResolver_CreatesGenericAskWithoutExecution()
    {
        var source = new MutationToolSource("mutate-without-policy");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);

        var outcome = await runtime.Host.InvokeToolAsync(
            new AgentToolCallRequest("call-1", source.ToolId, "{\"value\":1}"),
            assistantTurn: null);

        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
        Assert.Equal(AgentToolCallOutcomeKind.WaitingForApproval, outcome.Kind);
        Assert.Equal(AgentPermissionService.GenericMutationActionId, pending.ActionId);
        Assert.Equal(AgentPermissionService.GenericMutationBoundaryId, pending.BoundaryId);
        Assert.Equal(0, source.ExecutionCount);
        Assert.NotEmpty(pending.ExecutionFingerprint);
    }

    [Fact]
    public async Task StopWhileApprovalRevalidates_ExecutesNoToolAndLeavesTerminalRun()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        source.BlockPermissionResolution();

        var approval = runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId);
        await source.PermissionResolutionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = await runtime.StopCoordinator.StopAsync(runtime.Session.SessionId);
        source.ReleasePermissionResolution();
        await approval.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, source.ExecutionCount);
        Assert.Equal(AgentRunStatus.Stopped, stopped?.Status);
        Assert.Equal(AgentDurableRunStatus.Stopped, runtime.Store.GetRun(pending.RunId)?.Status);
        Assert.Equal(
            AgentPendingPermissionStatus.Expired,
            runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status);
    }

    [Fact]
    public async Task CancellationAfterApprovedExecutionStarts_InterruptsDurableRun()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        source.BlockExecution();

        var approval = runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId);
        await source.ExecutionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var active = runtime.ActiveRunRegistry.GetCurrent(
            runtime.Session.SessionId,
            pending.RunId,
            pending.RunRevision);
        Assert.NotNull(active);
        active!.CancellationTokenSource.Cancel();
        source.ReleaseExecution();

        var checkpoint = await approval.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentRunStatus.Interrupted, checkpoint?.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, runtime.Store.GetRun(pending.RunId)?.Status);
    }
}

internal sealed class PermissionHardeningRuntime : IAsyncDisposable
{
    private readonly RegressionTestPackageScope _scope;

    private PermissionHardeningRuntime(
        RegressionTestPackageScope scope,
        AgentLocalStore store,
        AgentSessionService sessionService,
        AgentWorkspaceService workspaceService,
        AgentPermissionService permissionService,
        AgentPermissionResumeCoordinator resumeCoordinator,
        AgentRunStopCoordinator stopCoordinator,
        AgentActiveRunRegistry activeRunRegistry,
        AgentBehaviorLoopHost host,
        AgentSessionRecord session,
        AgentWorkspaceRecord workspace)
    {
        _scope = scope;
        Store = store;
        SessionService = sessionService;
        WorkspaceService = workspaceService;
        PermissionService = permissionService;
        ResumeCoordinator = resumeCoordinator;
        StopCoordinator = stopCoordinator;
        ActiveRunRegistry = activeRunRegistry;
        Host = host;
        Session = session;
        Workspace = workspace;
    }

    public AgentLocalStore Store { get; }

    public AgentSessionService SessionService { get; }

    public AgentWorkspaceService WorkspaceService { get; }

    public AgentPermissionService PermissionService { get; }

    public AgentPermissionResumeCoordinator ResumeCoordinator { get; }

    public AgentRunStopCoordinator StopCoordinator { get; }

    public AgentActiveRunRegistry ActiveRunRegistry { get; }

    public AgentBehaviorLoopHost Host { get; }

    public AgentSessionRecord Session { get; }

    public AgentWorkspaceRecord Workspace { get; }

    public static async Task<PermissionHardeningRuntime> CreateAsync(
        MutationToolSource source,
        bool assignTool = true)
    {
        var scope = RegressionTestPackageScope.Create();
        try
        {
            var catalog = new RegressionTestExtensionCatalog();
            var provider = new PermissionHardeningProvider();
            catalog.AddExtension(PackageExtensionPoints.ChatProviders, provider);
            catalog.AddExtension(PackageExtensionPoints.ToolSources, source);
            if (source is IAgentPermissionSurface permissionSurface)
            {
                catalog.AddExtension(PackageExtensionPoints.PermissionSurfaces, permissionSurface);
            }

            catalog.AddExtension(
                PackageExtensionPoints.ExecutionTargets,
                new PermissionHardeningExecutionTarget(
                    PermissionHardeningExecutionTarget.PrimaryTargetId));
            catalog.AddExtension(
                PackageExtensionPoints.ExecutionTargets,
                new PermissionHardeningExecutionTarget(
                    PermissionHardeningExecutionTarget.SecondaryTargetId));

            var store = new AgentLocalStore(scope.Context);
            var sessionService = new AgentSessionService(store, catalog);
            var workspaceService = new AgentWorkspaceService(store, catalog, sessionService);
            var permissionService = new AgentPermissionService(store, catalog);
            var executionTargetService = new AgentExecutionTargetService(catalog);
            var toolService = new AgentToolService(
                new InstalledPackageToolSource(catalog),
                sessionService,
                workspaceService,
                executionTargetService,
                catalog);
            var profileService = new AgentProfileService(store, toolService, catalog);
            catalog.AddExtension(
                PackageExtensionPoints.RuntimeCatalogs,
                new AgentRuntimeCatalog(sessionService, profileService, workspaceService));

            var profile = await profileService.CreateProfileAsync("Permission hardening profile");
            profileService.SaveProfile(
                profile.ProfileId,
                profile.DisplayName,
                profile.Description,
                profile.Instructions,
                provider.Descriptor.ProviderId,
                PermissionHardeningProvider.ModelId,
                null,
                null,
                assignTool
                    ? [new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        source.ToolId,
                        source.SourceId)]
                    : [new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        "another-tool",
                        source.SourceId)]);
            profile = profileService.GetProfile(profile.ProfileId)!;

            var workspace = workspaceService.CreateWorkspace("Permission hardening workspace");
            workspaceService.SavePrimaryExecutionBinding(
                workspace.WorkspaceId,
                PermissionHardeningExecutionTarget.PrimaryTargetId);
            workspace = workspaceService.GetWorkspace(workspace.WorkspaceId)!;
            var session = sessionService.CreateSession(
                "Permission hardening session",
                profileId: profile.ProfileId,
                workspaceId: workspace.WorkspaceId);
            var run = sessionService.ReserveRun(session.SessionId, profile.ProfileId, "Run the tool");
            Assert.NotNull(store.TryTransitionRun(
                run.Key,
                run.Epoch,
                AgentRunStatus.Running,
                "Running."));
            var userTurn = sessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.User,
                "Run the tool");

            var memoryCoordinator = new AgentMemoryCoordinator(sessionService, catalog);
            var runEventLogger = new AgentRunEventLogger(scope.Context);
            var activeRunRegistry = new AgentActiveRunRegistry();
            var promptComposer = new AgentSystemPromptComposer(catalog);
            var defaultBehaviorLoop = new DefaultAgentBehaviorLoop(promptComposer);
            var hostFactory = new AgentBehaviorLoopHostFactory(
                sessionService,
                toolService,
                permissionService,
                memoryCoordinator,
                runEventLogger,
                activeRunRegistry,
                defaultBehaviorLoop);
            catalog.AddExtension(PackageExtensionPoints.BehaviorLoops, defaultBehaviorLoop);
            var behaviorLoopResolver = new AgentBehaviorLoopResolver(catalog, defaultBehaviorLoop);
            var providerResolver = new AgentRunProviderResolver(profileService, catalog);
            var childRunSessionService = new AgentChildRunSessionService(sessionService, profileService);
            var parentRunContinuationService = new AgentParentRunContinuationService(
                sessionService,
                profileService,
                workspaceService,
                providerResolver,
                activeRunRegistry,
                hostFactory,
                behaviorLoopResolver,
                childRunSessionService);
            var resumeCoordinator = new AgentPermissionResumeCoordinator(
                sessionService,
                workspaceService,
                profileService,
                permissionService,
                providerResolver,
                activeRunRegistry,
                runEventLogger,
                hostFactory,
                behaviorLoopResolver,
                parentRunContinuationService);
            var stopCoordinator = new AgentRunStopCoordinator(
                sessionService,
                permissionService,
                memoryCoordinator,
                activeRunRegistry,
                profileService);
            var host = hostFactory.Create(
                provider,
                session,
                profile,
                workspace,
                run.Key.RunId,
                run.Key.RunRevision,
                run.StartedAtUtc,
                run.UserMessage,
                userTurn.TurnId);

            return new PermissionHardeningRuntime(
                scope,
                store,
                sessionService,
                workspaceService,
                permissionService,
                resumeCoordinator,
                stopCoordinator,
                activeRunRegistry,
                host,
                session,
                workspace);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public async Task<AgentPendingPermissionRequestRecord> CreatePendingRequestAsync()
    {
        var outcome = await Host.InvokeToolAsync(
            new AgentToolCallRequest("call-1", "mutate", "{\"b\":2,\"a\":1}"),
            assistantTurn: null);
        Assert.Equal(AgentToolCallOutcomeKind.WaitingForApproval, outcome.Kind);
        return Assert.Single(PermissionService.ListPendingRequests(Session.SessionId));
    }

    public ValueTask DisposeAsync()
    {
        _scope.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal class MutationToolSource(string toolId) : IAgentToolSource
{
    private int _executionCount;
    private TaskCompletionSource? _executionStarted;
    private TaskCompletionSource? _executionRelease;

    public string ToolId { get; } = toolId;

    public int ExecutionCount => Volatile.Read(ref _executionCount);

    public bool ThrowOnExecution { get; init; }

    public string SourceId => "permission-hardening-source";

    public string DisplayName => "Permission Hardening Source";

    public string SourceKind => "test";

    protected AgentToolDescriptor Descriptor => new(
        ToolId,
        "Mutating Test Tool",
        "Mutates deterministic test state.",
        IsReadOnly: false,
        ArgumentsJsonSchema: "{\"type\":\"object\"}",
        SourceKind: SourceKind,
        SourceId: SourceId,
        SourceDisplayName: DisplayName);

    public Task ExecutionStarted => _executionStarted?.Task ?? Task.CompletedTask;

    public void BlockExecution()
    {
        _executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseExecution() => _executionRelease?.TrySetResult();

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Descriptor]);

    public ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string requestedToolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentToolReadiness?>(
            string.Equals(requestedToolId, ToolId, StringComparison.OrdinalIgnoreCase)
                ? new AgentToolReadiness(ToolId, AgentToolReadinessStatus.Ready, "Ready.")
                : null);

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _executionCount);
        _executionStarted?.TrySetResult();
        if (_executionRelease is not null)
        {
            await _executionRelease.Task.WaitAsync(cancellationToken);
        }

        if (ThrowOnExecution)
        {
            throw new InvalidOperationException("Injected mutating tool execution failure.");
        }

        return new AgentToolResult(ToolId, "Mutating tool executed.", Content: "ok");
    }
}

internal sealed class PermissionAwareMutationToolSource(string toolId)
    : MutationToolSource(toolId), IAgentPermissionAwareToolSource, IAgentPermissionSurface
{
    private int _permissionResolutionCount;
    private TaskCompletionSource? _permissionResolutionStarted;
    private TaskCompletionSource? _permissionResolutionRelease;

    public int PermissionResolutionCount => Volatile.Read(ref _permissionResolutionCount);

    public bool ThrowOnPermissionRevalidation { get; init; }

    public Task PermissionResolutionStarted =>
        _permissionResolutionStarted?.Task ?? Task.CompletedTask;

    public string SurfaceId => "permission-hardening-surface";

    public void BlockPermissionResolution()
    {
        _permissionResolutionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _permissionResolutionRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleasePermissionResolution() => _permissionResolutionRelease?.TrySetResult();

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolutionCount = Interlocked.Increment(ref _permissionResolutionCount);
        if (ThrowOnPermissionRevalidation && resolutionCount > 1)
        {
            throw new InvalidOperationException("Injected permission revalidation failure.");
        }
        if (_permissionResolutionRelease is not null)
        {
            _permissionResolutionStarted?.TrySetResult();
            await _permissionResolutionRelease.Task.WaitAsync(cancellationToken);
        }

        return new AgentPermissionRequest(
            "test.mutate",
            "test-boundary",
            "Allow the deterministic mutation?",
            ToolId: request.ToolId,
            WorkspaceId: context.Workspace?.WorkspaceId,
            BindingId: context.ExecutionBinding?.BindingId,
            ResourceDisplayName: "test state",
            ResourceReference: "test://state",
            IsMutation: true);
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        =>
        [
            new AgentPermissionActionDescriptor(
                "test.mutate",
                "Mutate test state",
                "Mutates deterministic test state.",
                [
                    new AgentPermissionBoundaryDescriptor(
                        "test-boundary",
                        "Test boundary",
                        "Requires approval.",
                        AgentPermissionDecision.Ask),
                ]),
        ];
}

internal sealed class PermissionHardeningProvider : IAgentChatProvider
{
    public const string ModelId = "permission-hardening-model";

    public AgentProviderDescriptor Descriptor { get; } = new(
        "permission-hardening-provider",
        "Permission Hardening Provider",
        [],
        SupportsStreaming: false,
        SupportsInterruptibleRuns: true);

    public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>(
            [new AgentModelDescriptor(ModelId, "Test Model", 8_192, 1_024)]);

    public ValueTask<AgentProviderReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentProviderReadiness(
            Descriptor.ProviderId,
            AgentProviderReadinessStatus.Ready,
            "Ready."));

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
        string? modelId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: false,
            SupportsStreamingToolCalls: false,
            SupportsMultipleToolCalls: false,
            "No continuation required."));

    public ValueTask<IChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class PermissionHardeningExecutionTarget(string targetId) : IAgentExecutionTarget
{
    public const string PrimaryTargetId = "permission-target-a";
    public const string SecondaryTargetId = "permission-target-b";

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "test-target",
        targetId,
        targetId,
        null,
        SupportsShell: false,
        SupportsFiles: false,
        SupportsSearch: false);

    public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

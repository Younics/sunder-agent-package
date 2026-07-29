using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using System.Text.Json;
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
    [Theory]
    [InlineData("{\"path\":\"one\",\"path\":\"two\"}")]
    [InlineData("{\"path\":\"one\",\"PATH\":\"two\"}")]
    public void ToolArguments_RejectDuplicateAndCaseCollidingProperties(string json)
    {
        Assert.False(AgentToolArgumentObject.TryParse(json, out _, out var error));
        Assert.Contains("colliding", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolArguments_RejectByteDepthAndPropertyBounds()
    {
        Assert.False(AgentToolArgumentObject.TryParse(
            "{\"value\":\"" + new string('x', AgentPayloadLimits.MaxToolArgumentBytes) + "\"}",
            out _,
            out var byteError));
        Assert.Contains("byte limit", byteError, StringComparison.OrdinalIgnoreCase);

        var nested = string.Concat(Enumerable.Repeat("{\"v\":", AgentPayloadLimits.MaxToolArgumentJsonDepth + 1))
                     + "null"
                     + new string('}', AgentPayloadLimits.MaxToolArgumentJsonDepth + 1);
        Assert.False(AgentToolArgumentObject.TryParse(nested, out _, out var depthError));
        Assert.Contains("valid JSON", depthError, StringComparison.OrdinalIgnoreCase);

        var properties = "{" + string.Join(',', Enumerable.Range(0, AgentPayloadLimits.MaxToolArgumentProperties + 1).Select(index => $"\"p{index}\":0")) + "}";
        Assert.False(AgentToolArgumentObject.TryParse(properties, out _, out var propertyError));
        Assert.Contains("property limit", propertyError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionTransitionGates_AreEvictedAfterUse()
    {
        var gates = new AgentSessionTransitionGate();
        for (var index = 0; index < 100; index++)
        {
            using var lease = await gates.EnterAsync(Guid.NewGuid());
        }

        Assert.Equal(0, gates.GateCount);
    }

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

        await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId,
            approveForSession: true);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(AgentPendingPermissionStatus.Expired, persisted?.Status);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Contains("context changed", persisted?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
    }

    [Fact]
    public async Task WorkspaceMutationDuringPermissionRevalidationExpiresWithoutExecution()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        source.BlockPermissionResolution();

        var approval = runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId,
            approveForSession: true);
        await source.PermissionResolutionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        runtime.WorkspaceService.SaveWorkspace(
            runtime.Workspace.WorkspaceId,
            "mutated during revalidation",
            runtime.Workspace.Description);
        source.ReleasePermissionResolution();
        await approval.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, source.ExecutionCount);
        Assert.Equal(
            AgentPendingPermissionStatus.Expired,
            runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status);
        Assert.Equal(
            AgentToolExecutionStatus.Failed,
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value)?.Status);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
    }

    [Fact]
    public async Task SessionApproval_IsPersistedOnlyWhenValidatedExecutionStarts()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        source.BlockPermissionResolution();
        source.BlockExecution();

        var approval = runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId,
            approveForSession: true);
        await source.PermissionResolutionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));

        source.ReleasePermissionResolution();
        await source.ExecutionStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var persistedApproval = Assert.Single(
            runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
        Assert.Equal(pending.ActionId, persistedApproval.ActionId);
        Assert.Equal(pending.BoundaryId, persistedApproval.Pattern);
        source.ReleaseExecution();
        await approval.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SessionApprovalInsertFailure_RollsBackExecutionStart()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        using (var connection = new SqliteConnection($"Data Source={runtime.Store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER RejectSessionApproval
                BEFORE INSERT ON AgentSessionPermissionApprovals
                BEGIN
                    SELECT RAISE(ABORT, 'injected approval failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var checkpoint = await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId,
            approveForSession: true);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(AgentRunStatus.Failed, checkpoint?.Status);
        Assert.Equal(AgentPendingPermissionStatus.Failed, persisted?.Status);
        Assert.Null(persisted?.ExecutionStartedAtUtc);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
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
        Assert.Equal(runtime.Store.GetRun(pending.RunId)?.StartedAtUtc, active!.StartedAtUtc);
        active.CancellationTokenSource.Cancel();
        source.ReleaseExecution();

        var checkpoint = await approval.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentRunStatus.Interrupted, checkpoint?.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, runtime.Store.GetRun(pending.RunId)?.Status);
        var execution = Assert.IsType<AgentToolExecutionRecord>(
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value));
        Assert.Equal(AgentToolExecutionStatus.Ambiguous, execution.Status);
        Assert.Contains("may have occurred", execution.OutcomeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfiguredResourceClaim_RebindsExactOwnersAfterProcessLocalStateIsLost()
    {
        var source = new PermissionAwareMutationToolSource(
            "mutate",
            AgentPermissionBoundaryIds.ConfiguredScope,
            includeResourceClaim: true);
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        Assert.Single(pending.ResourceClaims);
        runtime.ForgetPreparedInvocation(pending);

        var checkpoint = await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId);

        Assert.Equal(1, source.ExecutionCount);
        Assert.Equal(AgentRunStatus.Completed, checkpoint?.Status);
        Assert.Equal(
            AgentToolExecutionStatus.Completed,
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value)?.Status);
    }

    [Fact]
    public async Task OutsideResourceClaim_RequiresReapprovalAfterProcessLocalCapabilityIsLost()
    {
        var source = new PermissionAwareMutationToolSource(
            "mutate",
            AgentPermissionBoundaryIds.OutsideConfiguredScope,
            includeResourceClaim: true);
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        Assert.Single(pending.ResourceClaims);
        runtime.ForgetPreparedInvocation(pending);

        await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId);

        Assert.Equal(0, source.ExecutionCount);
        var execution = Assert.IsType<AgentToolExecutionRecord>(
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value));
        var result = Assert.IsType<AgentToolResult>(
            runtime.Store.GetToolExecutionResult(execution.ExecutionId));
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
        Assert.Equal(AgentToolResultErrorCodes.PermissionReapprovalRequired, result.ErrorCode);
    }

    [Fact]
    public async Task DuplicateApprovedDispatch_ReturnsLedgerResultWithoutBeginningOrReexecuting()
    {
        var source = new PermissionAwareMutationToolSource(
            "mutate",
            AgentPermissionBoundaryIds.ConfiguredScope,
            includeResourceClaim: true);
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId);
        var beginCount = 0;

        var duplicate = await runtime.Host.HandleApprovedToolCallAsync(
            pending,
            CancellationToken.None,
            _ =>
            {
                Interlocked.Increment(ref beginCount);
                return ValueTask.FromResult(true);
            });

        Assert.Equal(AgentToolCallOutcomeKind.Executed, duplicate.Kind);
        Assert.Equal("ok", duplicate.Result?.Content);
        Assert.Equal(0, beginCount);
        Assert.Equal(1, source.ExecutionCount);
    }

    [Fact]
    public void PermissionFingerprint_PersistsClaimsButExcludesTransientCapabilities()
    {
        var runId = Guid.NewGuid();
        var descriptor = new AgentToolDescriptor(
            "read",
            "Read",
            "Read a file.",
            SourceKind: "workspace",
            SourceId: "files");
        var claim = new AgentResourceClaim(
            1,
            "local-host-resource-claim-v1",
            "/workspace/file.txt",
            "/workspace",
            new string('a', 64),
            true,
            "RegularFile",
            "identity-one",
            false,
            true,
            "files.read",
            "workspace",
            "workspace-generation",
            "binding",
            "binding-generation",
            "call-1",
            0,
            "sunder.package.agent.tools.files",
            "sunder.package.agent.execution.local");
        var first = new AgentPermissionRequest(
            "files.read",
            AgentPermissionBoundaryIds.ConfiguredScope,
            "Read file")
        {
            ResourceClaims = [claim],
            ResourceCapabilities = ["transient-capability-one"],
        };
        var second = first with
        {
            ResourceCapabilities = ["transient-capability-two"],
        };

        var firstFingerprint = AgentPermissionFingerprint.Create(
            runId,
            1,
            descriptor,
            "call-1",
            "{}",
            null,
            null,
            null,
            first);
        var secondFingerprint = AgentPermissionFingerprint.Create(
            runId,
            1,
            descriptor,
            "call-1",
            "{}",
            null,
            null,
            null,
            second);
        var changedClaimFingerprint = AgentPermissionFingerprint.Create(
            runId,
            1,
            descriptor,
            "call-1",
            "{}",
            null,
            null,
            null,
            first with { ResourceClaims = [claim with { TargetIdentity = "identity-two" }] });
        var snapshot = AgentPermissionFingerprint.CreateExecutionSnapshot(
            runId,
            1,
            descriptor,
            "call-1",
            "{}",
            null,
            null,
            null,
            first,
            null,
            null,
            null);
        var operationJson = JsonSerializer.Serialize(new AgentResourceOperationContext(
            runId,
            1,
            "call-1",
            "files.read",
            0,
            "workspace-generation",
            "binding-generation",
            "sunder.package.agent.tools.files",
            "sunder.package.agent.execution.local",
            "authority-activation-secret"));
        var resolvedJson = JsonSerializer.Serialize(new AgentResolvedResource(
            "file",
            "/workspace/file.txt",
            "stable-reference",
            AgentPermissionBoundaryIds.ConfiguredScope,
            true)
        {
            ResourceClaim = claim,
            AuthorityReferences = ["resolved-authority-secret"],
            DeleteAuthorityReferences = ["delete-authority-secret"],
        });

        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.NotEqual(firstFingerprint, changedClaimFingerprint);
        Assert.Contains("local-host-resource-claim-v1", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("transient-capability-one", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("authority-activation-secret", operationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("resolved-authority-secret", resolvedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("delete-authority-secret", resolvedJson, StringComparison.Ordinal);
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
        AgentToolService toolService,
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
        ToolService = toolService;
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

    public AgentToolService ToolService { get; }

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
                toolService,
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

    public void ForgetPreparedInvocation(AgentPendingPermissionRequestRecord pending)
        => ToolService.ReleasePreparedInvocation(
            pending.ToolExecutionId
            ?? throw new InvalidOperationException("The pending request has no tool execution."));

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

internal sealed class PermissionAwareMutationToolSource(
    string toolId,
    string boundaryId = "test-boundary",
    bool includeResourceClaim = false)
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

        var permission = new AgentPermissionRequest(
            "test.mutate",
            boundaryId,
            "Allow the deterministic mutation?",
            ToolId: request.ToolId,
            WorkspaceId: context.Workspace?.WorkspaceId,
            BindingId: context.ExecutionBinding?.BindingId,
            ResourceDisplayName: "test state",
            ResourceReference: "test://state",
            IsMutation: true);
        if (!includeResourceClaim)
        {
            return permission;
        }

        var operation = context.ResourceOperation
            ?? throw new InvalidOperationException("Resource claim planning requires an invocation binding.");
        var claim = new AgentResourceClaim(
            1,
            "permission-hardening-resource-v1",
            "/test/resource",
            string.Equals(boundaryId, AgentPermissionBoundaryIds.ConfiguredScope, StringComparison.Ordinal)
                ? "/test"
                : null,
            new string('c', 64),
            true,
            "RegularFile",
            "stable-test-target",
            false,
            true,
            "test.mutate",
            context.Workspace?.WorkspaceId ?? string.Empty,
            operation.WorkspaceGeneration,
            context.ExecutionBinding?.BindingId ?? string.Empty,
            operation.BindingGeneration,
            operation.ToolCallId,
            0,
            operation.ToolOwnerPackageId,
            operation.ExecutionTargetOwnerPackageId);
        return permission with
        {
            ResourceClaims = [claim],
            ResourceCapabilities = string.Equals(
                                      boundaryId,
                                      AgentPermissionBoundaryIds.OutsideConfiguredScope,
                                      StringComparison.Ordinal)
                                  && operation.CanIssueOutsideAuthority
                ? [$"test-resource-authority-v1:{operation.AuthorityActivationId}"]
                : [],
        };
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
                        boundaryId,
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
        SupportsFiles: false);

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

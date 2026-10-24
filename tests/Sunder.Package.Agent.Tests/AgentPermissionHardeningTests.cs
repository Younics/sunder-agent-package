using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPermissionHardeningTests
{
    [Fact]
    public async Task PermissionDefaults_DoNotBlockPolicyEditsAndUseTheLatestOverride()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var store = new AgentLocalStore(scope.Context);
        var surface = new DeferredPermissionSurface();
        await using var surfaces = new FakeServiceCatalog<AgentServices.IPermissionSurfaceService>(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.PermissionSurfaces));
        surfaces.Add(surface);
        var permissions = new AgentPermissionService(store, surfaces);
        var evaluation = Task.Run(() => permissions.EvaluateAsync(
            null,
            new AgentPermissionRequest("test.mutate", "test.boundary", "Mutation")));
        try
        {
            await surface.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => permissions.SaveOverride(
                "test.mutate", "test.boundary", AgentPermissionDecision.Deny))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(evaluation.IsCompleted);
        }
        finally
        {
            surface.Release.TrySetResult();
            await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var result = await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentPermissionDecision.Deny, result.Decision);
        Assert.Equal(AgentPermissionDecisionSource.ConfiguredOverride, result.Source);
    }

    [Fact]
    public async Task PermissionDefaults_CancellationDoesNotBecomeAnUnknownBoundaryDecision()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var store = new AgentLocalStore(scope.Context);
        using var cancellation = new CancellationTokenSource();
        var surface = new DeferredPermissionSurface();
        await using var surfaces = new FakeServiceCatalog<AgentServices.IPermissionSurfaceService>(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.PermissionSurfaces));
        surfaces.Add(surface);
        var permissions = new AgentPermissionService(store, surfaces);
        var evaluation = Task.Run(() => permissions.EvaluateAsync(
            null,
            new AgentPermissionRequest("test.mutate", "test.boundary", "Mutation"),
            cancellation.Token));
        try
        {
            await surface.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => evaluation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            surface.Release.TrySetResult();
            try
            {
                await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Theory]
    [InlineData("{\"path\":\"one\",\"path\":\"two\"}")]
    [InlineData("{\"path\":\"one\",\"PATH\":\"two\"}")]
    public void ToolArguments_RejectDuplicateAndCaseCollidingProperties(string json)
    {
        Assert.False(AgentToolArgumentObject.TryParse(json, out _, out var error));
        Assert.Contains("colliding", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolArguments_AcceptBeyondFormerByteCapAndRejectParserComplexityBounds()
    {
        const int formerMaximumBytes = 1024 * 1024;
        Assert.True(AgentToolArgumentObject.TryParse(
            "{\"value\":\"" + new string('x', formerMaximumBytes) + "\"}",
            out var largeArguments,
            out var largeError),
            largeError);
        Assert.True(largeArguments!.TryReadRequiredString("value", out var largeValue, out _));
        Assert.Equal(formerMaximumBytes, largeValue!.Length);

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
    public async Task ApprovedExecutionFailure_PersistsAmbiguousTerminalState()
    {
        var source = new PermissionAwareMutationToolSource("mutate") { ThrowOnExecution = true };
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();

        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(1, source.ExecutionCount);
        Assert.Equal(AgentPendingPermissionStatus.Failed, persisted?.Status);
        Assert.NotNull(persisted?.DecidedAtUtc);
        Assert.Contains("dispatched", persisted?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            AgentToolExecutionStatus.Ambiguous,
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value)?.Status);
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
    public async Task ChangedExecutionTargetConfiguration_ExpiresApprovalWithoutExecution()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        var readinessCount = source.ReadinessCount;
        runtime.PrimaryExecutionTarget.AdvanceConfiguration();

        await runtime.ResumeCoordinator.ApproveAsync(
            runtime.Session.SessionId,
            pending.RequestId,
            approveForSession: true);

        var persisted = runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId);
        Assert.Equal(AgentPendingPermissionStatus.Expired, persisted?.Status);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Equal(readinessCount, source.ReadinessCount);
        Assert.Contains("configuration changed", persisted?.DecisionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
    }

    [Fact]
    public async Task ExecutionTargetConfigurationMutationDuringPlanningDispatchesNoTool()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        source.BlockPermissionResolution();
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);

        var invocation = runtime.Host.InvokeToolAsync(
            new AgentToolCallRequest("call-1", source.ToolId, "{}"),
            assistantTurn: null).AsTask();
        await source.PermissionResolutionStarted.WaitAsync(TimeSpan.FromSeconds(10));
        runtime.PrimaryExecutionTarget.AdvanceConfiguration();
        source.ReleasePermissionResolution();
        var outcome = await invocation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentToolCallOutcomeKind.Denied, outcome.Kind);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
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
    public async Task ArgumentValidationFailure_ReturnsCorrectiveResultWithoutPermissionOrDispatch()
    {
        var source = new PermissionAwareMutationToolSource("mutate")
        {
            ArgumentValidationResult = new("mutate", "Missing replacement text.",
                Content: "Supply oldString and newString; read arguments cannot be used for an edit.",
                IsError: true, ErrorCode: "files-arguments-invalid"),
        };
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);

        var outcome = await runtime.Host.InvokeToolAsync(new("call-invalid", source.ToolId, "{\"path\":\"file\"}"), null);

        Assert.Equal(AgentToolCallOutcomeKind.Executed, outcome.Kind);
        Assert.True(outcome.Result?.IsError);
        Assert.Equal("files-arguments-invalid", outcome.Result?.ErrorCode);
        Assert.Equal(0, source.PermissionResolutionCount);
        Assert.Equal(0, source.ExecutionCount);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
        var execution = Assert.Single(runtime.Store.ListToolExecutions(runtime.Session.SessionId));
        Assert.Null(execution.StartedAtUtc);
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
    }

    [Fact]
    public async Task ArgumentValidationFailure_PairsRemainingCallsWithoutClaimingTheRunTerminated()
    {
        var source = new PermissionAwareMutationToolSource("mutate")
        {
            ArgumentValidationResult = new("mutate", "Missing replacement text.",
                Content: "Supply oldString and newString.", IsError: true, ErrorCode: "files-arguments-invalid"),
        };
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var outcomes = await runtime.Host.InvokeToolsAsync(
            [new("invalid-call", source.ToolId, "{}"), new("remaining-call", source.ToolId, "{\"other\":true}")], null);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal(AgentToolCallOutcomeKind.Executed, outcome.Kind));
        Assert.Equal("files-arguments-invalid", outcomes[0].Result?.ErrorCode);
        Assert.Contains("corrective", outcomes[1].Result?.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminated the run", outcomes[1].Result?.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentRunStatus.Running, runtime.SessionService.GetLatestCheckpoint(runtime.Session.SessionId)?.Status);
        Assert.Equal(0, source.PermissionResolutionCount);
        Assert.Equal(0, source.ExecutionCount);
        var ledger = runtime.Store.ListToolExecutions(runtime.Session.SessionId);
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, execution => Assert.Null(execution.StartedAtUtc));
        Assert.Empty(runtime.PermissionService.ListPendingRequests(runtime.Session.SessionId));
    }

    [Fact]
    public async Task ApprovalIntent_SurvivesClaimRecoveryWithoutGrantingPermissionEarly()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        var actions = await runtime.PermissionService.ListActionsAsync();
        var (first, _) = runtime.PermissionService.TryClaimPendingRequestForApproval(
            runtime.Session.SessionId, pending.RequestId, actions, approveForSession: true);
        Assert.True(first.Request?.ApproveForSession);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
        using (var connection = new SqliteConnection($"Data Source={runtime.Store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE AgentPendingPermissionRequests SET ClaimLeaseExpiresAtUtc = $past WHERE RequestId = $id;";
            command.Parameters.AddWithValue("$past", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
            command.Parameters.AddWithValue("$id", pending.RequestId);
            command.ExecuteNonQuery();
        }
        var (recovered, _) = runtime.PermissionService.TryClaimPendingRequestForApproval(
            runtime.Session.SessionId, pending.RequestId, actions);
        Assert.True(recovered.Request?.ApproveForSession);
        Assert.NotEqual(first.Request?.ClaimToken, recovered.Request?.ClaimToken);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
    }

    [Fact]
    public async Task SessionApproval_IsReusedForEquivalentRequestsButNeverOverridesDeny()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId, approveForSession: true);

        var equivalent = new AgentPermissionRequest(pending.ActionId, pending.BoundaryId, "Another invocation", ToolId: source.ToolId);
        var evaluation = await runtime.PermissionService.EvaluateAsync(runtime.Session.SessionId, equivalent);
        Assert.Equal(AgentPermissionDecision.Allow, evaluation.Decision);
        Assert.Equal(AgentPermissionDecisionSource.SessionApproval, evaluation.Source);
        runtime.PermissionService.SaveOverride(pending.ActionId, pending.BoundaryId, AgentPermissionDecision.Deny);
        Assert.Equal(AgentPermissionDecision.Deny,
            (await runtime.PermissionService.EvaluateAsync(runtime.Session.SessionId, equivalent)).Decision);
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
        await runtime.ForgetPreparedInvocation(pending);

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
        await runtime.ForgetPreparedInvocation(pending);

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
            (_, _) =>
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
            "younics.ai.agent.tools.files",
            "younics.ai.agent.execution.local");
        var first = new AgentPermissionRequest(
            "files.read",
            AgentPermissionBoundaryIds.ConfiguredScope,
            "Read file")
        {
            ScopeClassificationBasis = AgentPermissionScopeClassificationBasis.OpenedAncestorIdentity,
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
        var changedTargetConfigurationFingerprint = AgentPermissionFingerprint.Create(
            runId,
            1,
            descriptor,
            "call-1",
            "{}",
            null,
            null,
            null,
            first,
            executionTargetConfigurationGeneration: "target-config-two");
        var originalTargetConfigurationFingerprint = AgentPermissionFingerprint.Create(
            runId,
            1,
            descriptor,
            "call-1",
            "{}",
            null,
            null,
            null,
            first,
            executionTargetConfigurationGeneration: "target-config-one");
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
            null,
            permissionEvaluation: new AgentPermissionEvaluation(
                AgentPermissionDecision.Ask,
                "Using configured decision.")
            {
                BaseDecision = AgentPermissionDecision.Ask,
                Source = AgentPermissionDecisionSource.ConfiguredOverride,
            });
        var operationJson = JsonSerializer.Serialize(new AgentResourceOperationContext(
            runId,
            1,
            "call-1",
            "files.read",
            0,
            "workspace-generation",
            "binding-generation",
            "younics.ai.agent.tools.files",
            "younics.ai.agent.execution.local",
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
        Assert.NotEqual(originalTargetConfigurationFingerprint, changedTargetConfigurationFingerprint);
        Assert.Contains("local-host-resource-claim-v1", snapshot, StringComparison.Ordinal);
        using var snapshotDocument = JsonDocument.Parse(snapshot);
        Assert.Equal(
            (int)AgentPermissionScopeClassificationBasis.OpenedAncestorIdentity,
            snapshotDocument.RootElement
                .GetProperty("permissionRequest")
                .GetProperty("scopeClassificationBasis")
                .GetInt32());
        Assert.Equal(
            (int)AgentPermissionDecisionSource.ConfiguredOverride,
            snapshotDocument.RootElement
                .GetProperty("decisionAudit")
                .GetProperty("source")
                .GetInt32());
        Assert.DoesNotContain("transient-capability-one", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("authority-activation-secret", operationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("resolved-authority-secret", resolvedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("delete-authority-secret", resolvedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingActionId_UsesUnknownBoundaryDecisionSource()
    {
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(
            new MutationToolSource("mutate"));

        var evaluation = await runtime.PermissionService.EvaluateAsync(
            runtime.Session.SessionId,
            new AgentPermissionRequest(string.Empty, AgentPermissionBoundaryIds.Unknown, "Malformed request"));

        Assert.Equal(AgentPermissionDecision.Deny, evaluation.Decision);
        Assert.Equal(AgentPermissionDecisionSource.UnknownBoundary, evaluation.Source);
    }

    private sealed class DeferredPermissionSurface : AgentServices.IPermissionSurfaceService
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentPermissionSurfaceDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new AgentPermissionSurfaceDescriptor("test.permissions", "Test permissions",
            [
                new("test.mutate", "Mutate", "Controlled default",
                    [new("test.boundary", "Boundary", "Controlled boundary", AgentPermissionDecision.Allow)]),
            ]);
        }
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
        AgentWorkspaceRecord workspace,
        PermissionHardeningExecutionTarget primaryExecutionTarget)
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
        PrimaryExecutionTarget = primaryExecutionTarget;
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

    public PermissionHardeningExecutionTarget PrimaryExecutionTarget { get; }

    public static async Task<PermissionHardeningRuntime> CreateAsync(
        MutationToolSource source,
        bool assignTool = true)
    {
        var scope = RegressionTestPackageScope.Create();
        try
        {
            var catalog = new RegressionTestExtensionCatalog();
            var provider = new PermissionHardeningProvider();
            catalog.AddProvider(RegressionTestBindings.ChatProviders, provider);
            catalog.AddProvider(RegressionTestBindings.ToolSources, source);
            if (source is IAgentPermissionSurface permissionSurface)
            {
                catalog.AddProvider(RegressionTestBindings.PermissionSurfaces, permissionSurface);
            }

            var primaryExecutionTarget = new PermissionHardeningExecutionTarget(
                PermissionHardeningExecutionTarget.PrimaryTargetId);
            catalog.AddProvider(
                RegressionTestBindings.ExecutionTargets,
                primaryExecutionTarget);
            catalog.AddProvider(
                RegressionTestBindings.ExecutionTargets,
                new PermissionHardeningExecutionTarget(
                    PermissionHardeningExecutionTarget.SecondaryTargetId));

            var store = new AgentLocalStore(scope.Context);
            var sessionService = new AgentSessionService(store, catalog.SessionCleaners);
            var workspaceService = new AgentWorkspaceService(store, catalog.SessionCleaners, sessionService);
            var permissionService = new AgentPermissionService(store, catalog.PermissionSurfaces);
            var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
            var toolService = new AgentToolService(
                sessionService,
                workspaceService,
                executionTargetService,
                catalog.ToolSources,
                catalog.ObservedToolExecutions);
            var profileService = catalog.CreateProfileService(store, toolService);
            catalog.AddRuntimeCatalog(new AgentRuntimeCatalog(sessionService, profileService, workspaceService));

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
            var executionBinding = workspaceService.SavePrimaryExecutionBinding(
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

            var memoryCoordinator = new AgentMemoryCoordinator(sessionService, catalog.PromptContextContributors,
                new AgentLifecycleDispatcher(store, catalog.DurableLifecycleObservers));
            var runEventLogger = new AgentRunEventLogger(scope.Context);
            var activeRunRegistry = catalog.ActiveRuns;
            var promptComposer = new AgentSystemPromptComposer(catalog.SystemPromptContributors);
            var defaultBehaviorLoop = new DefaultAgentBehaviorLoop(promptComposer);
            var hostFactory = new AgentBehaviorLoopHostFactory(
                sessionService,
                toolService,
                permissionService,
                memoryCoordinator,
                runEventLogger,
                activeRunRegistry,
                defaultBehaviorLoop);
            var behaviorLoopResolver = new AgentBehaviorLoopResolver(
                catalog.BehaviorLoops,
                catalog.RunControls,
                activeRunRegistry,
                defaultBehaviorLoop);
            var providerResolver = new AgentRunProviderResolver(profileService, catalog.ChatProviders);
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
                await providerResolver.ResolveChatProviderAsync(profile),
                session,
                profile,
                workspace,
                run.Key.RunId,
                run.Key.RunRevision,
                run.StartedAtUtc,
                run.UserMessage,
                userTurn.TurnId,
                executionBinding);

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
                workspace,
                primaryExecutionTarget);
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

    public ValueTask ForgetPreparedInvocation(AgentPendingPermissionRequestRecord pending)
        => ToolService.ReleasePreparedInvocationAsync(
            pending.ToolExecutionId
            ?? throw new InvalidOperationException("The pending request has no tool execution."));

    public ValueTask DisposeAsync()
    {
        _scope.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal class MutationToolSource(string toolId) : IAgentToolSource, IRegressionTestArgumentValidator
{
    private int _executionCount;
    private int _readinessCount;
    private TaskCompletionSource? _executionStarted;
    private TaskCompletionSource? _executionRelease;

    public string ToolId { get; } = toolId;

    public int ExecutionCount => Volatile.Read(ref _executionCount);

    public int ReadinessCount => Volatile.Read(ref _readinessCount);

    public bool ThrowOnExecution { get; init; }

    public AgentToolResult? ArgumentValidationResult { get; init; }

    public Func<AgentToolRequest, AgentToolResult?>? ArgumentValidator { get; init; }

    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ArgumentValidator is { } validator ? validator(request) : ArgumentValidationResult);
    }

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
    {
        Interlocked.Increment(ref _readinessCount);
        return ValueTask.FromResult<AgentToolReadiness?>(
            string.Equals(requestedToolId, ToolId, StringComparison.OrdinalIgnoreCase)
                ? new AgentToolReadiness(ToolId, AgentToolReadinessStatus.Ready, "Ready.")
                : null);
    }

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

    public bool OmitPermissionRequest { get; set; }

    public AgentPermissionRequest? PermissionOverride { get; set; }

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

        if (OmitPermissionRequest) return null;
        if (PermissionOverride is { } permissionOverride) return permissionOverride;
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
    private int _configurationRevision = 1;

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "test-target",
        targetId,
        targetId,
        null,
        SupportsShell: false,
        SupportsFiles: false);

    public void AdvanceConfiguration() => Interlocked.Increment(ref _configurationRevision);

    public ValueTask<string?> GetConfigurationGenerationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(
            Volatile.Read(ref _configurationRevision).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

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

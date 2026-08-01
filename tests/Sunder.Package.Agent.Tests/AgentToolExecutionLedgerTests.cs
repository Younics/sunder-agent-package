extern alias AgentCore;

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentToolExecutionLedgerTests
{
    [Fact]
    public void PrepareBatch_PersistsEveryCallAndTranscriptItemAtomically()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var preparations = new[]
        {
            Prepare("call-1", "read_file", "{\"path\":\"one\"}", isReadOnly: true),
            Prepare("call-2", "write_file", "{\"path\":\"two\"}", isReadOnly: false),
        };

        var persisted = Assert.IsAssignableFrom<IReadOnlyList<AgentToolExecutionPreparationResult>>(
            store.TryPrepareToolExecutions(run.Key, running.Run.Epoch, preparations));

        Assert.Equal(2, persisted.Count);
        Assert.All(store.ListToolExecutions(session.SessionId), execution =>
            Assert.Equal(AgentToolExecutionStatus.Prepared, execution.Status));
        var callItems = store.ListTurns(session.SessionId)
            .SelectMany(turn => turn.Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolCall)
            .ToArray();
        Assert.Equal(2, callItems.Length);
        Assert.All(callItems, item =>
        {
            Assert.NotNull(item.ToolExecutionId);
            Assert.Equal(AgentToolExecutionStatus.Prepared, item.ToolExecutionStatus);
        });

        Assert.Throws<InvalidOperationException>(() => store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "read_file", "{\"path\":\"changed\"}", isReadOnly: true)]));
        Assert.Equal(2, store.ListToolExecutions(session.SessionId).Count);
        Assert.Equal(2, store.ListTurns(session.SessionId).Count);
    }

    [Fact]
    public void ExecutionProvenance_SurvivesRestartAndProjectsFromTheAuthoritativeLedger()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare(
                "call-1",
                "read",
                "{\"path\":\"src/Owned.cs\"}",
                isReadOnly: true,
                ownerPackageId: "sunder.package.agent.tools.files",
                toolSchemaId: "files.read",
                toolSchemaVersion: "1",
                executionTargetOwnerPackageId: "sunder.package.agent.execution.local")])!);

        var restarted = new AgentLocalStore(scope.Context);
        var execution = Assert.IsType<AgentToolExecutionRecord>(
            restarted.GetToolExecution(prepared.Execution.ExecutionId));
        Assert.Equal("sunder.package.agent.tools.files", execution.OwnerPackageId);
        Assert.Equal("files.read", execution.ToolSchemaId);
        Assert.Equal("1", execution.ToolSchemaVersion);
        Assert.Equal("sunder.package.agent.execution.local", execution.ExecutionTargetOwnerPackageId);
        var item = Assert.Single(restarted.ListTurns(session.SessionId)).Items[0];
        Assert.Equal(execution.ExecutionId, item.ToolExecutionId);
        Assert.Equal(execution.OwnerPackageId, item.ToolOwnerPackageId);
        Assert.Equal(execution.ToolSchemaId, item.ToolSchemaId);
        Assert.Equal(execution.ToolSchemaVersion, item.ToolSchemaVersion);
    }

    [Fact]
    public void DispatchAndCompletion_UseCasAndPersistExactlyOneLinkedResult()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "read_file", "{}", isReadOnly: true)])!);

        var started = Assert.IsType<AgentToolExecutionStartResult>(store.TryStartToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            prepared.Execution.InvocationFingerprint));
        Assert.Equal(AgentToolExecutionStatus.Started, started.Execution.Status);
        Assert.Null(store.TryStartToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            prepared.Execution.InvocationFingerprint));

        var completed = Assert.IsType<AgentToolExecutionCompletionResult>(store.TryCompleteToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            AgentToolExecutionStatus.Completed,
            new AgentToolResult("read_file", new string('s', 3000), Content: "result"),
            new string('c', 200)));

        Assert.Equal(AgentToolExecutionStatus.Completed, completed.Execution.Status);
        Assert.Equal(AgentLocalStore.MaxToolExecutionOutcomeCodeLength, completed.Execution.OutcomeCode?.Length);
        Assert.Equal(AgentLocalStore.MaxToolExecutionOutcomeSummaryLength, completed.Execution.OutcomeSummary?.Length);
        var resultItems = store.ListTurns(session.SessionId)
            .SelectMany(turn => turn.Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolResult)
            .ToArray();
        var resultItem = Assert.Single(resultItems);
        Assert.Equal(prepared.Execution.ExecutionId, resultItem.ToolExecutionId);
        Assert.Equal(AgentToolExecutionStatus.Completed, resultItem.ToolExecutionStatus);
        Assert.Null(store.TryCompleteToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            AgentToolExecutionStatus.Completed,
            new AgentToolResult("read_file", "duplicate"),
            "duplicate"));
        Assert.Single(
            store.ListTurns(session.SessionId)
                .SelectMany(turn => turn.Items),
            item => item.Kind == AgentTurnItemKind.ToolResult);
        var recorded = Assert.IsType<AgentToolResult>(
            store.GetToolExecutionResult(prepared.Execution.ExecutionId));
        Assert.Equal("read_file", recorded.ToolId);
        Assert.Equal("result", recorded.Content);
        Assert.False(recorded.IsError);
        Assert.False(recorded.RequiresPromptContextRefresh);
    }

    [Fact]
    public async Task StartedExecution_LiveHeaderMatchesAuthoritativeDetailAndExpands()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("live-call", "read_file", "{\"path\":\"live.txt\"}", isReadOnly: true)])!);

        var started = Assert.IsType<AgentToolExecutionStartResult>(store.TryStartToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            prepared.Execution.InvocationFingerprint));
        var projectedTurn = CorePresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(
            started.ToolCallTurn);
        var projectedItem = Assert.Single(projectedTurn.Items);
        var detail = Assert.IsType<AgentTranscriptToolDetailRecord>(store.GetTranscriptToolDetail(new(
            session.SessionId,
            projectedItem.ToolExecutionId,
            projectedItem.CallId,
            projectedItem.ItemId,
            projectedTurn.RunId,
            projectedTurn.RunRevision)));

        Assert.Equal(started.Execution.UpdatedAtUtc, started.ToolCallTurn.UpdatedAtUtc);
        Assert.Equal(AgentToolExecutionStatus.Started, projectedItem.ToolExecutionStatus);
        Assert.Equal(detail.Revision, projectedItem.ToolDetailRevision);
        Assert.True(projectedItem.ToolHasDetails);

        var projection = CorePresentation.TranscriptRowProjector<AgentTranscriptRowViewModel>
            .DescribeTool(projectedTurn, projectedItem);
        using var state = new CorePresentation.TranscriptToolDetailState(
            projection,
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(store.GetTranscriptToolDetail(request));
            },
            new AgentToolPresentationService().Resolve);
        var request = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var loaded = await state.LoadAsync(request, CancellationToken.None);

        Assert.True(state.TryMaterialize(request, loaded, out var materialized));
        Assert.NotNull(materialized);
        Assert.True(state.CommitExpanded(request));
        Assert.Equal(CorePresentation.TranscriptToolExpansionState.Expanded, state.State);
        Assert.Equal("{\"path\":\"live.txt\"}", loaded?.ArgumentsJson);
        Assert.Equal(AgentToolExecutionStatus.Started, loaded?.Status);
    }

    [Theory]
    [InlineData(false, AgentToolExecutionStatus.Completed)]
    [InlineData(true, AgentToolExecutionStatus.Failed)]
    public void ReplacingChildSuspensionResult_AtomicallyRefreshesLedgerAndReloadedDetailStatus(
        bool isError,
        AgentToolExecutionStatus expectedStatus)
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("child-call", "delegate_tasks", "{\"task\":\"review\"}", isReadOnly: false)])!);
        Assert.NotNull(store.TryStartToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            prepared.Execution.InvocationFingerprint));
        var childSessionId = Guid.NewGuid();
        var suspension = Assert.IsType<AgentToolExecutionSuspensionResult>(
            store.SuspendRunAndCompleteToolExecution(
                run.Key,
                running.Run.Epoch,
                new AgentChildJoinRunSuspension(
                    Guid.NewGuid(),
                    "delegate_tasks",
                    "{\"task\":\"review\"}",
                    [new AgentChildJoinTask(childSessionId, "child-call", "Reviewer")],
                    []),
                "Waiting for child.",
                prepared.Execution.ExecutionId,
                new AgentToolResult("delegate_tasks", "Subagent work is continuing."),
                "child-suspension-durable"));
        var waitingRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        var finalSummary = isError
            ? "One or more subagent tasks ended without completing."
            : "Subagent tasks completed.";
        var finalResult = new AgentToolResult(
            "delegate_tasks",
            finalSummary,
            Content: isError ? "Reviewer failed." : "Reviewer completed.",
            IsError: isError,
            ErrorCode: isError ? AgentToolResultErrorCodes.SubagentRunFailed : null);
        var transcriptEpoch = store.GetTranscriptEpoch(session.SessionId);

        var replaced = store.TryReplaceChildSuspensionToolResult(
            run.Key,
            waitingRun.Epoch,
            "child-call",
            finalResult);

        Assert.NotNull(replaced);
        Assert.Equal(transcriptEpoch + 1, store.GetTranscriptEpoch(session.SessionId));
        Assert.Null(store.GetLatestSessionContextCheckpoint(session.SessionId));
        var restarted = new AgentLocalStore(scope.Context);
        var execution = Assert.IsType<AgentToolExecutionRecord>(
            restarted.GetToolExecution(prepared.Execution.ExecutionId));
        Assert.Equal(expectedStatus, execution.Status);
        Assert.Equal(finalSummary, execution.OutcomeSummary);
        Assert.True(execution.UpdatedAtUtc > suspension.Completion.Execution.UpdatedAtUtc);
        var headerTurn = Assert.Single(
            restarted.ListRecentTranscriptHeaders(session.SessionId, 10),
            turn => turn.Kind == AgentTurnKind.ToolResult);
        var header = Assert.Single(headerTurn.Items);
        Assert.Equal(expectedStatus, header.ToolExecutionStatus);
        Assert.Equal(isError ? finalSummary : null, header.ToolErrorSummary);
        Assert.Equal(isError ? null : finalSummary, header.ToolHeaderHint);
        var detail = Assert.IsType<AgentTranscriptToolDetailRecord>(
            restarted.GetTranscriptToolDetail(new(
                session.SessionId,
                prepared.Execution.ExecutionId)));
        Assert.Equal(expectedStatus, detail.Status);
        Assert.Equal(isError, detail.IsError);
        Assert.Equal(finalSummary, detail.ResultSummary);
        Assert.Equal(
            (execution.UpdatedAtUtc.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10,
            detail.Revision);
    }

    [Fact]
    public void ReadOnlyCacheHit_CanCompleteFromPreparedButMutationCannot()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [
                Prepare("read-call", "read_file", "{}", isReadOnly: true),
                Prepare("write-call", "write_file", "{}", isReadOnly: false),
            ])!;

        Assert.NotNull(store.TryCompleteToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared[0].Execution.ExecutionId,
            AgentToolExecutionStatus.Completed,
            new AgentToolResult("read_file", "Reused safe result."),
            "read-only-cache-hit",
            allowPreparedReadOnlyCompletion: true));
        Assert.Null(store.TryCompleteToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared[1].Execution.ExecutionId,
            AgentToolExecutionStatus.Completed,
            new AgentToolResult("write_file", "Must not complete."),
            "cache-hit",
            allowPreparedReadOnlyCompletion: true));
        Assert.Equal(
            AgentToolExecutionStatus.Prepared,
            store.ListToolExecutions(session.SessionId).Single(item => item.CallId == "write-call").Status);
    }

    [Fact]
    public void TerminalRun_FailsPreparedAndMarksStartedAmbiguousWithPairedResults()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [
                Prepare("started-call", "mutate", "{}", isReadOnly: false),
                Prepare("prepared-call", "read", "{}", isReadOnly: true),
            ])!;
        Assert.NotNull(store.TryStartToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared[0].Execution.ExecutionId,
            prepared[0].Execution.InvocationFingerprint));
        Assert.Null(store.TryTransitionRun(
            run.Key,
            running.Run.Epoch,
            AgentRunStatus.Completed,
            "Must reject open calls."));

        var interrupted = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            running.Run.Epoch,
            AgentRunStatus.Interrupted,
            "Interrupted."));

        Assert.Equal(2, interrupted.ToolResultTurns.Count);
        var executions = store.ListToolExecutions(session.SessionId).ToDictionary(item => item.CallId);
        Assert.Equal(AgentToolExecutionStatus.Ambiguous, executions["started-call"].Status);
        Assert.Equal(AgentToolExecutionStatus.Failed, executions["prepared-call"].Status);
        Assert.Contains("may have occurred", executions["started-call"].OutcomeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            2,
            store.ListTurns(session.SessionId).Count(turn => turn.Kind == AgentTurnKind.ToolResult));
    }

    [Fact]
    public void StartupRecovery_IsIdempotentAndDoesNotReplayStartedExecution()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "mutate", "{}", isReadOnly: false)])!);
        Assert.NotNull(store.TryStartToolExecution(
            run.Key,
            running.Run.Epoch,
            prepared.Execution.ExecutionId,
            prepared.Execution.InvocationFingerprint));

        var recovered = new AgentLocalStore(scope.Context);
        recovered.RecoverRuntimeState();
        var execution = Assert.Single(recovered.ListToolExecutions(session.SessionId));
        Assert.Equal(AgentToolExecutionStatus.Ambiguous, execution.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, recovered.GetRun(run.Key.RunId)?.Status);
        Assert.Single(recovered.ListTurns(session.SessionId), turn => turn.Kind == AgentTurnKind.ToolResult);

        var recoveredAgain = new AgentLocalStore(scope.Context);
        recoveredAgain.RecoverRuntimeState();
        Assert.Equal(AgentToolExecutionStatus.Ambiguous, Assert.Single(recoveredAgain.ListToolExecutions(session.SessionId)).Status);
        Assert.Single(recoveredAgain.ListTurns(session.SessionId), turn => turn.Kind == AgentTurnKind.ToolResult);
    }

    [Fact]
    public void StartupRecovery_PreservesPreparedPermissionSuspension()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "mutate", "{}", isReadOnly: false)])!);
        var request = new AgentPendingPermissionRequestRecord(
            "request-1",
            session.SessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            "profile",
            Guid.NewGuid(),
            "message",
            "call-1",
            "test.action",
            "test.boundary",
            "Approve mutation",
            "mutate",
            "{}",
            null,
            null,
            "workspace",
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            ExecutionFingerprint: new string('a', 64),
            ExecutionSnapshotJson: "{}")
        {
            ToolExecutionId = prepared.Execution.ExecutionId,
        };
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));

        var recovered = new AgentLocalStore(scope.Context);
        recovered.RecoverRuntimeState();

        Assert.Equal(AgentToolExecutionStatus.Prepared, Assert.Single(recovered.ListToolExecutions(session.SessionId)).Status);
        Assert.Equal(AgentPendingPermissionStatus.Pending, recovered.GetPermissionRequest(session.SessionId, request.RequestId)?.Status);
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, recovered.GetRun(run.Key.RunId)?.Status);
        Assert.DoesNotContain(recovered.ListTurns(session.SessionId), turn => turn.Kind == AgentTurnKind.ToolResult);
    }

    [Fact]
    public void StartupRecovery_PreservesUnconsumedClaimForPreparedPermission()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "mutate", "{}", isReadOnly: false)])!);
        var request = CreatePermissionRequest(session, run, prepared.Execution);
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));
        Assert.True(store.TryClaimPendingPermissionRequest(session.SessionId, request.RequestId).IsClaimed);

        var recovered = new AgentLocalStore(scope.Context);
        recovered.RecoverRuntimeState();

        Assert.Equal(
            AgentPendingPermissionStatus.Claimed,
            recovered.GetPermissionRequest(session.SessionId, request.RequestId)?.Status);
        Assert.Equal(
            AgentToolExecutionStatus.Prepared,
            recovered.GetToolExecution(prepared.Execution.ExecutionId)?.Status);
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, recovered.GetRun(run.Key.RunId)?.Status);
        Assert.DoesNotContain(recovered.ListTurns(session.SessionId), turn => turn.Kind == AgentTurnKind.ToolResult);
    }

    [Fact]
    public void LinkedPermissionCompletion_PreservesOwnerAcrossRestartAndCommitsResultTogether()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare(
                "call-1",
                "mutate",
                "{}",
                isReadOnly: false,
                ownerPackageId: "sunder.package.agent.tools.files")])!);
        var request = CreatePermissionRequest(session, run, prepared.Execution);
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));
        var restarted = new AgentLocalStore(scope.Context);
        var claimed = Assert.IsType<AgentPendingPermissionRequestRecord>(
            restarted.TryClaimPendingPermissionRequest(session.SessionId, request.RequestId).Request);
        var suspendedRun = Assert.IsType<AgentDurableRunRecord>(restarted.GetRun(run.Key.RunId));
        Assert.NotNull(restarted.ResumeClaimedPermissionRequest(claimed, suspendedRun.Epoch));
        Assert.True(restarted.MarkClaimedPermissionExecutionStarted(
            session.SessionId,
            request.RequestId,
            claimed.ClaimToken!));
        var resumedRun = Assert.IsType<AgentDurableRunRecord>(restarted.GetRun(run.Key.RunId));

        var completion = restarted.TryCompleteToolExecution(
            run.Key,
            resumedRun.Epoch,
            prepared.Execution.ExecutionId,
            AgentToolExecutionStatus.Completed,
            new AgentToolResult("mutate", "Mutation completed.", Content: "done"),
            "tool-completed",
            permissionRequest: claimed,
            permissionStatus: AgentPendingPermissionStatus.Executed);

        Assert.NotNull(completion);
        var persistedPermission = Assert.IsType<AgentPendingPermissionRequestRecord>(
            restarted.GetPermissionRequest(session.SessionId, request.RequestId));
        Assert.Equal(AgentPendingPermissionStatus.Executed, persistedPermission.Status);
        Assert.Null(persistedPermission.ExecutionStartedAtUtc);
        Assert.Equal(
            AgentToolExecutionStatus.Completed,
            restarted.GetToolExecution(prepared.Execution.ExecutionId)?.Status);
        var resultItem = Assert.Single(
            restarted.ListTurns(session.SessionId).SelectMany(turn => turn.Items),
            item => item.ToolExecutionId == prepared.Execution.ExecutionId
                    && item.Kind == AgentTurnItemKind.ToolResult);
        Assert.Equal("sunder.package.agent.tools.files", resultItem.ToolOwnerPackageId);
    }

    [Fact]
    public void StartupRecovery_ReconcilesTerminalLedgerWithClaimedPermission()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "mutate", "{}", isReadOnly: false)])!);
        var request = CreatePermissionRequest(session, run, prepared.Execution);
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));
        var claimed = Assert.IsType<AgentPendingPermissionRequestRecord>(
            store.TryClaimPendingPermissionRequest(session.SessionId, request.RequestId).Request);
        var suspendedRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        Assert.NotNull(store.ResumeClaimedPermissionRequest(claimed, suspendedRun.Epoch));
        Assert.True(store.MarkClaimedPermissionExecutionStarted(
            session.SessionId,
            request.RequestId,
            claimed.ClaimToken!));
        var resumedRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        Assert.NotNull(store.TryCompleteToolExecution(
            run.Key,
            resumedRun.Epoch,
            prepared.Execution.ExecutionId,
            AgentToolExecutionStatus.Completed,
            new AgentToolResult("mutate", "Mutation completed."),
            "tool-completed"));
        Assert.Equal(
            AgentPendingPermissionStatus.Claimed,
            store.GetPermissionRequest(session.SessionId, request.RequestId)?.Status);

        var recovered = new AgentLocalStore(scope.Context);
        recovered.RecoverRuntimeState();

        Assert.Equal(
            AgentPendingPermissionStatus.Executed,
            recovered.GetPermissionRequest(session.SessionId, request.RequestId)?.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, recovered.GetRun(run.Key.RunId)?.Status);
        Assert.Equal(
            AgentToolExecutionStatus.Completed,
            recovered.GetToolExecution(prepared.Execution.ExecutionId)?.Status);
        Assert.Single(recovered.ListTurns(session.SessionId), turn => turn.Kind == AgentTurnKind.ToolResult);
    }

    [Fact]
    public void PermissionLink_RejectsDifferentInvocationArguments()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "mutate", "{\"value\":1}", isReadOnly: false)])!);
        var request = CreatePermissionRequest(
            session,
            run,
            prepared.Execution,
            argumentsJson: "{\"value\":2}");

        Assert.Throws<InvalidOperationException>(() =>
            store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));
        Assert.Empty(store.ListPendingPermissionRequests(session.SessionId));
        Assert.Equal(AgentDurableRunStatus.Running, store.GetRun(run.Key.RunId)?.Status);
    }

    [Fact]
    public void StartupRecovery_TerminalizesPreparedExecutionForMalformedPermissionSuspension()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "mutate", "{}", isReadOnly: false)])!);
        var request = new AgentPendingPermissionRequestRecord(
            "request-1",
            session.SessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            "profile",
            Guid.NewGuid(),
            "message",
            "call-1",
            "test.action",
            "test.boundary",
            "Approve mutation",
            "mutate",
            "{}",
            null,
            null,
            "workspace",
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            ExecutionFingerprint: string.Empty,
            ExecutionSnapshotJson: string.Empty)
        {
            ToolExecutionId = prepared.Execution.ExecutionId,
        };
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));

        var recovered = new AgentLocalStore(scope.Context);
        recovered.RecoverRuntimeState();

        Assert.Equal(
            AgentToolExecutionStatus.Failed,
            Assert.Single(recovered.ListToolExecutions(session.SessionId)).Status);
        Assert.Equal(
            AgentPendingPermissionStatus.Failed,
            recovered.GetPermissionRequest(session.SessionId, request.RequestId)?.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, recovered.GetRun(run.Key.RunId)?.Status);
        Assert.Single(recovered.ListTurns(session.SessionId), turn => turn.Kind == AgentTurnKind.ToolResult);
    }

    [Fact]
    public void PermissionDenial_TerminalizesEveryPreparedExecutionInRun()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var (session, run, running) = CreateRunningRun(store);
        var prepared = store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [
                Prepare("permission-call", "mutate", "{}", isReadOnly: false),
                Prepare("later-call", "read", "{}", isReadOnly: true),
            ])!;
        var request = new AgentPendingPermissionRequestRecord(
            "request-1",
            session.SessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            "profile",
            Guid.NewGuid(),
            "message",
            "permission-call",
            "test.action",
            "test.boundary",
            "Approve mutation",
            "mutate",
            "{}",
            null,
            null,
            "workspace",
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            ExecutionFingerprint: new string('a', 64),
            ExecutionSnapshotJson: "{}")
        {
            ToolExecutionId = prepared[0].Execution.ExecutionId,
        };
        Assert.NotNull(store.SavePendingPermissionRequestAndSuspendRun(request, running.Run.Epoch));

        var denied = store.TryDenyPendingPermissionRequest(
            session.SessionId,
            request.RequestId,
            "Permission denied.");

        Assert.True(denied.IsDecided);
        Assert.All(
            store.ListToolExecutions(session.SessionId),
            execution => Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status));
        Assert.Equal(
            2,
            store.ListTurns(session.SessionId).Count(turn => turn.Kind == AgentTurnKind.ToolResult));
        Assert.Single(denied.Finalization?.ToolResultTurns ?? []);
    }

    [Fact]
    public void TranscriptRollbackRetainsLedgerAndFullSessionDeletionRemovesIt()
    {
        using var scope = DurableRunTestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Ledger rollback");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var anchor = store.AppendUserTurn(session.SessionId, AgentMessageRole.User, "request", []);
        var run = store.ReserveRun(session.SessionId, "profile", "request");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [Prepare("call-1", "read", "{}", isReadOnly: true)])!);

        store.RollbackTranscript(session.SessionId, anchor.TurnId);

        Assert.Single(store.ListToolExecutions(session.SessionId));
        Assert.Empty(store.ListTurns(session.SessionId));
        store.DeleteSessionTree(session.SessionId);
        Assert.Empty(store.ListToolExecutions(session.SessionId));
    }

    [Fact]
    public async Task TranscriptIdentityAndUiPreferExecutionIdAndKeepAmbiguousDetailsLazy()
    {
        var executionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "Unknown outcome",
            "reused-call-id",
            "mutate",
            "{}",
            "Effects may have occurred.",
            null,
            null,
            false,
            true,
            "tool-execution-ambiguous",
            null)
        {
            ToolExecutionId = executionId,
            ToolExecutionStatus = AgentToolExecutionStatus.Ambiguous,
        };
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [item],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var other = item with { ToolExecutionId = Guid.NewGuid() };

        Assert.NotEqual(
            CorePresentation.TranscriptRowProjector<AgentTranscriptRowViewModel>.DescribeTool(turn, item).AnchorKey,
            CorePresentation.TranscriptRowProjector<AgentTranscriptRowViewModel>.DescribeTool(turn, other).AnchorKey);
        using var row = TranscriptToolTestHarness.CreateAgentRow(
            turn,
            item,
            new AgentToolPresentationService());
        Assert.Equal("Ambiguous", row.StatusText);
        Assert.False(row.IsExpanded);
        Assert.False(row.HasMaterializedDetails);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);

        Assert.True(details.HasAmbiguousWarning);
        Assert.Contains("may have occurred", details.AmbiguousWarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not retry", details.AmbiguousWarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptPresentation_ProjectsFiveLedgerStatesAndPreservesLegacyRunning()
    {
        var turnId = Guid.NewGuid();
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var legacyItem = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolCall,
            null,
            "legacy-call",
            "read_file",
            "{}",
            null,
            null,
            null,
            false,
            false,
            null,
            null);
        var legacyProjection = CorePresentation.TranscriptRowProjector<AgentTranscriptRowViewModel>
            .DescribeTool(turn, legacyItem);
        using var legacyRow = TranscriptToolTestHarness.CreateAgentRow(
            turn,
            legacyItem,
            new AgentToolPresentationService());

        Assert.Equal("Running", legacyProjection.StatusText);
        Assert.Equal("i", legacyProjection.StatusIconText);
        Assert.Equal("Running", legacyRow.StatusText);
        Assert.True(legacyRow.IsRunning);

        foreach (var status in Enum.GetValues<AgentToolExecutionStatus>())
        {
            var item = legacyItem with
            {
                ToolExecutionId = Guid.NewGuid(),
                ToolExecutionStatus = status,
            };
            var projection = CorePresentation.TranscriptRowProjector<AgentTranscriptRowViewModel>
                .DescribeTool(turn, item);
            Assert.Equal(status.ToString(), projection.StatusText);
            Assert.Equal(
                status is AgentToolExecutionStatus.Failed or AgentToolExecutionStatus.Ambiguous
                    ? CorePresentation.TranscriptProjectedRowKind.Error
                    : CorePresentation.TranscriptProjectedRowKind.ToolCall,
                projection.Kind);
            Assert.Equal(
                status switch
                {
                    AgentToolExecutionStatus.Completed => "✓",
                    AgentToolExecutionStatus.Started => "i",
                    AgentToolExecutionStatus.Prepared => "·",
                    _ => "!",
                },
                projection.StatusIconText);
        }

        var durableSuspension = legacyItem with
        {
            Kind = AgentTurnItemKind.ToolResult,
            IsError = true,
            ToolExecutionId = Guid.NewGuid(),
            ToolExecutionStatus = AgentToolExecutionStatus.Completed,
        };
        Assert.Equal(
            CorePresentation.TranscriptProjectedRowKind.ToolResult,
            CorePresentation.TranscriptRowProjector<AgentTranscriptRowViewModel>
                .DescribeTool(turn, durableSuspension).Kind);
    }

    private static AgentToolExecutionPreparation Prepare(
        string callId,
        string toolId,
        string argumentsJson,
        bool isReadOnly,
        string? ownerPackageId = null,
        string? toolSchemaId = null,
        string? toolSchemaVersion = null,
        string? executionTargetOwnerPackageId = null)
        => new(
            new AgentToolCallRequest(callId, toolId, argumentsJson),
            isReadOnly,
            AgentToolInvocationFingerprint.Create(toolId, argumentsJson),
            ownerPackageId,
            toolSchemaId,
            toolSchemaVersion,
            executionTargetOwnerPackageId);

    private static AgentPendingPermissionRequestRecord CreatePermissionRequest(
        AgentSessionRecord session,
        AgentDurableRunRecord run,
        AgentToolExecutionRecord execution,
        string argumentsJson = "{}")
        => new(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            "profile",
            Guid.NewGuid(),
            "message",
            execution.CallId,
            "test.action",
            "test.boundary",
            "Approve mutation",
            execution.ToolId,
            argumentsJson,
            null,
            null,
            "workspace",
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            ExecutionFingerprint: new string('a', 64),
            ExecutionSnapshotJson: "{}")
        {
            ToolExecutionId = execution.ExecutionId,
        };

    private static (AgentSessionRecord Session, AgentDurableRunRecord Run, AgentRunTransitionResult Running)
        CreateRunningRun(AgentLocalStore store)
    {
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Tool ledger tests");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", "message");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running."));
        return (session, run, running);
    }
}

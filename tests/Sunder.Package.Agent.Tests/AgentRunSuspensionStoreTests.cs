using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRunSuspensionStoreTests
{
    [Fact]
    public void ContinuationToken_IsConsumedOnlyOnce()
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, "single use");
        var suspended = Assert.IsType<AgentRunSuspensionResult>(store.SuspendRun(
            run.Key,
            new AgentPermissionRunSuspension("request-1", "call-1", userTurnId),
            "Waiting for permission."));

        var resumed = store.ConsumeRunContinuation(
            run.Key,
            suspended.ContinuationToken,
            AgentRunSuspensionKind.Permission,
            "Permission approved.");
        var replayed = store.ConsumeRunContinuation(
            run.Key,
            suspended.ContinuationToken,
            AgentRunSuspensionKind.Permission,
            "Replay.");

        Assert.NotNull(resumed);
        Assert.Null(replayed);
        var persisted = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        Assert.Equal(AgentDurableRunStatus.Running, persisted.Status);
        Assert.Null(persisted.Suspension);
        Assert.Null(persisted.ContinuationToken);
    }

    [Fact]
    public void ChildJoin_RejectsStaleParentRevisionAndChangedRunId()
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, "stale parent");
        var childSessionId = Guid.NewGuid();
        var suspended = SuspendChildJoin(store, run, userTurnId, childSessionId);
        var completion = CompletedChild(childSessionId, AgentRunStatus.Completed);

        var staleRevision = store.CompleteChildJoinTask(
            run.Key with { RunRevision = run.Key.RunRevision + 1 },
            suspended.ContinuationToken,
            completion);
        var changedRun = store.CompleteChildJoinTask(
            run.Key with { RunId = Guid.NewGuid() },
            suspended.ContinuationToken,
            completion);

        Assert.Equal(AgentChildJoinTransitionOutcome.Rejected, staleRevision.Outcome);
        Assert.Equal(AgentChildJoinTransitionOutcome.Rejected, changedRun.Outcome);
        Assert.IsType<AgentChildJoinRunSuspension>(store.GetRun(run.Key.RunId)?.Suspension);
    }

    [Fact]
    public void ChildJoin_RejectsRunSupersededByNewerRevision()
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, "superseded parent");
        var childSessionId = Guid.NewGuid();
        var suspended = SuspendChildJoin(store, run, userTurnId, childSessionId);
        _ = store.ReserveRun(run.Key.SessionId, "profile", "newer message");

        var transition = store.CompleteChildJoinTask(
            run.Key,
            suspended.ContinuationToken,
            CompletedChild(childSessionId, AgentRunStatus.Completed));

        Assert.Equal(AgentChildJoinTransitionOutcome.Rejected, transition.Outcome);
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, store.GetRun(run.Key.RunId)?.Status);
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Stopped)]
    public void ChildJoin_TerminalChildOutcomeCompletesJoinAndResumesParent(AgentRunStatus childStatus)
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, childStatus.ToString());
        var childSessionId = Guid.NewGuid();
        var suspended = SuspendChildJoin(store, run, userTurnId, childSessionId);

        var transition = store.CompleteChildJoinTask(
            run.Key,
            suspended.ContinuationToken,
            CompletedChild(childSessionId, childStatus));

        Assert.True(transition.IsReady);
        var result = Assert.Single(transition.Suspension!.CompletedTasks);
        Assert.Equal(childStatus, result.Status);
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, store.GetRun(run.Key.RunId)?.Status);
        Assert.Equal(AgentParentContinuationWorkStatus.Ready, transition.Work?.Status);
    }

    [Fact]
    public void ChildJoin_FinalContinuationIsRecoverableBeforeTokenConsumption()
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, "recoverable parent");
        var childSessionId = Guid.NewGuid();
        var suspended = SuspendChildJoin(store, run, userTurnId, childSessionId);
        var ready = store.CompleteChildJoinTask(
            run.Key,
            suspended.ContinuationToken,
            CompletedChild(childSessionId, AgentRunStatus.Completed));
        Assert.True(ready.IsReady);

        var restarted = new AgentLocalStore(scope.Context);
        var work = Assert.Single(restarted.ListDispatchableParentContinuationWork());
        var waitingRun = Assert.IsType<AgentDurableRunRecord>(restarted.GetRun(run.Key.RunId));
        var dispatch = restarted.TryClaimParentContinuationWork(
            work.WorkId,
            run.Key,
            waitingRun.Epoch,
            suspended.ContinuationToken);

        Assert.NotNull(dispatch);
        Assert.Equal(AgentParentContinuationWorkStatus.Dispatching, dispatch!.Work.Status);
        Assert.Equal(AgentDurableRunStatus.Running, restarted.GetRun(run.Key.RunId)?.Status);
        Assert.Null(restarted.GetRun(run.Key.RunId)?.ContinuationToken);
    }

    [Fact]
    public void ParentContinuationClaim_RejectsNewerRunRevision()
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, "stale dispatch");
        var childSessionId = Guid.NewGuid();
        var suspended = SuspendChildJoin(store, run, userTurnId, childSessionId);
        var ready = store.CompleteChildJoinTask(
            run.Key,
            suspended.ContinuationToken,
            CompletedChild(childSessionId, AgentRunStatus.Completed));
        var waitingRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        _ = store.ReserveRun(run.Key.SessionId, "profile", "newer message");

        var dispatch = store.TryClaimParentContinuationWork(
            ready.Work!.WorkId,
            run.Key,
            waitingRun.Epoch,
            suspended.ContinuationToken);

        Assert.Null(dispatch);
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, store.GetRun(run.Key.RunId)?.Status);
        Assert.Equal(
            AgentParentContinuationWorkStatus.Ready,
            Assert.Single(store.ListDispatchableParentContinuationWork()).Status);
    }

    [Fact]
    public void ParentContinuationRejectedQueue_RemainsDurablyDispatchable()
    {
        using var scope = RegressionTestPackageScope.Create();
        var (store, run, userTurnId) = CreateRunningRun(scope, "retry after queue rejection");
        var childSessionId = Guid.NewGuid();
        var suspended = SuspendChildJoin(store, run, userTurnId, childSessionId);
        var ready = store.CompleteChildJoinTask(
            run.Key,
            suspended.ContinuationToken,
            CompletedChild(childSessionId, AgentRunStatus.Completed));
        var waitingRun = Assert.IsType<AgentDurableRunRecord>(store.GetRun(run.Key.RunId));
        var dispatch = Assert.IsType<AgentParentContinuationDispatchResult>(
            store.TryClaimParentContinuationWork(
                ready.Work!.WorkId,
                run.Key,
                waitingRun.Epoch,
                suspended.ContinuationToken));

        Assert.True(store.RecordParentContinuationRetryPending(
            dispatch.Work.WorkId,
            "Background queue rejected dispatch."));

        var restarted = new AgentLocalStore(scope.Context);
        var pending = Assert.Single(restarted.ListDispatchableParentContinuationWork());
        Assert.Equal(AgentParentContinuationWorkStatus.Dispatching, pending.Status);
        Assert.Null(pending.ExecutionStartedAtUtc);
        Assert.Equal("Background queue rejected dispatch.", pending.LastError);
    }

    private static (AgentLocalStore Store, AgentDurableRunRecord Run, Guid UserTurnId) CreateRunningRun(
        RegressionTestPackageScope scope,
        string userMessage)
    {
        var store = new AgentLocalStore(scope.Context);
        var workspace = new AgentWorkspaceService(store).CreateWorkspace("Suspension tests");
        var session = store.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        var run = store.ReserveRun(session.SessionId, "profile", userMessage);
        store.SaveCheckpoint(session.SessionId, run.Key.RunRevision, AgentRunStatus.Running, "Running.");
        return (store, run, Guid.NewGuid());
    }

    private static AgentRunSuspensionResult SuspendChildJoin(
        AgentLocalStore store,
        AgentDurableRunRecord run,
        Guid userTurnId,
        Guid childSessionId)
        => Assert.IsType<AgentRunSuspensionResult>(store.SuspendRun(
            run.Key,
            new AgentChildJoinRunSuspension(
                userTurnId,
                "task",
                "{}",
                [new AgentChildJoinTask(childSessionId, "call-1", "Child")],
                []),
            "Waiting for child."));

    private static AgentChildJoinTaskResult CompletedChild(
        Guid childSessionId,
        AgentRunStatus status)
        => new(
            childSessionId,
            "call-1",
            status,
            status.ToString(),
            $"Child {status}.",
            "Child");
}

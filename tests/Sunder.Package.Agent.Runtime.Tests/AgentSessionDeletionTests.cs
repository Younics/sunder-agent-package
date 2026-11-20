using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentSessionDeletionTests
{
    [Fact]
    public void DeletionFence_OverlappingLeasesRemainFencedUntilLastLeaseExits()
    {
        var fence = new AgentSessionDeletionFence();
        var sessionId = Guid.NewGuid();
        const string workspaceId = "workspace";
        var session = new AgentSessionRecord(
            sessionId,
            "Session",
            AgentSessionState.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkspaceId: workspaceId);
        using var first = fence.Enter([sessionId], workspaceId);
        var second = fence.Enter([sessionId], workspaceId);

        first.Dispose();

        Assert.True(fence.IsFenced(session));
        Assert.True(fence.IsWorkspaceFenced(workspaceId));

        second.Dispose();

        Assert.False(fence.IsFenced(session));
        Assert.False(fence.IsWorkspaceFenced(workspaceId));
    }

    [Fact]
    public async Task DeleteSession_StopsAndDrainsChildRunBeforeDeletingTree()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateRuntime(scope);
        var workspace = runtime.Workspaces.CreateWorkspace("Delete session workspace");
        var root = runtime.Sessions.CreateSession(
            "Root",
            profileId: "profile",
            workspaceId: workspace.WorkspaceId);
        var child = runtime.Sessions.CreateSession(
            "Child",
            parentSessionId: root.SessionId,
            rootSessionId: root.SessionId,
            profileId: "profile",
            workspaceId: workspace.WorkspaceId);
        var active = StartTrackedRun(runtime, child.SessionId);

        var deletion = runtime.Deletion.DeleteSessionAsync(root.SessionId);
        await WaitUntilAsync(() => active.CancellationTokenSource.IsCancellationRequested);

        Assert.NotNull(runtime.Sessions.GetSession(root.SessionId));
        Assert.NotNull(runtime.Sessions.GetSession(child.SessionId));
        Assert.Equal(AgentDurableRunStatus.Stopped, runtime.Sessions.GetRun(active.RunId)?.Status);

        runtime.ActiveRuns.Complete(child.SessionId, active.RunId, active.RunRevision);
        await deletion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(runtime.Sessions.GetSession(root.SessionId));
        Assert.Null(runtime.Sessions.GetSession(child.SessionId));
        active.CancellationTokenSource.Dispose();
    }

    [Fact]
    public async Task DeleteWorkspace_DrainsRunBeforeDeletingWorkspacePersistence()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateRuntime(scope);
        var workspace = runtime.Workspaces.CreateWorkspace("Delete workspace");
        var session = runtime.Sessions.CreateSession(
            "Session",
            profileId: "profile",
            workspaceId: workspace.WorkspaceId);
        var active = StartTrackedRun(runtime, session.SessionId);

        var deletion = runtime.Deletion.DeleteWorkspaceAsync(workspace.WorkspaceId);
        await WaitUntilAsync(() => active.CancellationTokenSource.IsCancellationRequested);

        Assert.NotNull(runtime.Workspaces.GetWorkspace(workspace.WorkspaceId));
        Assert.NotNull(runtime.Sessions.GetSession(session.SessionId));

        runtime.ActiveRuns.Complete(session.SessionId, active.RunId, active.RunRevision);
        await deletion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(runtime.Workspaces.GetWorkspace(workspace.WorkspaceId));
        Assert.Null(runtime.Sessions.GetSession(session.SessionId));
        active.CancellationTokenSource.Dispose();
    }

    private static AgentActiveRunHandle StartTrackedRun(
        DeletionRuntime runtime,
        Guid sessionId)
    {
        var run = runtime.Sessions.ReserveRun(sessionId, "profile", "message");
        var lease = new AgentDurableRunLease(run);
        Assert.NotNull(runtime.Sessions.TryTransitionRun(
            lease,
            AgentRunStatus.Running,
            "Running."));
        var active = new AgentActiveRunHandle(
            run.Key.RunId,
            run.Key.RunRevision,
            run.StartedAtUtc,
            run.ProfileId,
            run.UserMessage,
            new CancellationTokenSource())
        {
            DurableLease = lease,
        };
        Assert.True(runtime.ActiveRuns.Activate(sessionId, active).IsAccepted);
        return active;
    }

    private static DeletionRuntime CreateRuntime(RegressionTestPackageScope scope)
    {
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store);
        var workspaces = new AgentWorkspaceService(store, sessionService: sessions);
        var activeRuns = new AgentActiveRunRegistry();
        var transitionGate = new AgentSessionTransitionGate();
        var deletionFence = new AgentSessionDeletionFence();
        return new DeletionRuntime(
            sessions,
            workspaces,
            activeRuns,
            new AgentSessionDeletionService(
                sessions,
                workspaces,
                activeRuns,
                transitionGate,
                deletionFence));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for run cancellation.");
            }
            await Task.Delay(10);
        }
    }

    private sealed record DeletionRuntime(
        AgentSessionService Sessions,
        AgentWorkspaceService Workspaces,
        AgentActiveRunRegistry ActiveRuns,
        AgentSessionDeletionService Deletion);
}

extern alias AgentCore;

using System.Collections.Concurrent;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPresentationThreadAffinityTests
{
    [Theory]
    [InlineData("once", false)]
    [InlineData("session", false)]
    [InlineData("deny", false)]
    [InlineData("once", true)]
    [InlineData("session", true)]
    [InlineData("deny", true)]
    public async Task PermissionClickDismissesBeforeAwaitAndGuardsAllThreeDecisions(string action, bool snapshots)
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var sessions = new DeferredTranscriptGateway(turnIsAvailable: true);
        var request = CreatePermissionRequest() with { SessionId = sessions.Turn.SessionId };
        FallbackPermissionGateway permissions = snapshots ? new SnapshotPermissionGateway(request) : new(request);
        var runs = new DeferredPermissionRunGateway(permissions);
        using var model = new AgentChatViewModel(new StaticProfileGateway(), new StaticWorkspaceGateway(),
            sessions, permissions, runs, dispatcher);
        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = model.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        var collectionThreads = new ConcurrentQueue<int>();
        model.PendingPermissionRequests.CollectionChanged += (_, _) => collectionThreads.Enqueue(Environment.CurrentManagedThreadId);
        Task operation = Task.CompletedTask;
        await dispatcher.InvokeAsync(() =>
        {
            Assert.Single(model.PendingPermissionRequests);
            operation = action switch
            {
                "once" => model.ApprovePermissionCommand.ExecuteAsync(request),
                "session" => model.ApprovePermissionForSessionCommand.ExecuteAsync(request),
                _ => model.DenyPermissionCommand.ExecuteAsync(request),
            };
            Assert.Empty(model.PendingPermissionRequests);
            Assert.False(model.HasPendingPermissionRequests);
            Assert.False(operation.IsCompleted);
        });
        try
        {
            await runs.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task repeated = Task.CompletedTask;
            await dispatcher.InvokeAsync(() => repeated = Task.WhenAll(
                model.ApprovePermissionCommand.ExecuteAsync(request),
                model.ApprovePermissionForSessionCommand.ExecuteAsync(request),
                model.DenyPermissionCommand.ExecuteAsync(request)));
            await repeated.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, runs.MutationCount);
        }
        finally { runs.ReleaseMutation.TrySetResult(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(model.PendingPermissionRequests);
        Assert.NotEmpty(collectionThreads);
        Assert.All(collectionThreads, thread => Assert.Equal(dispatcher.ThreadId, thread));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedPermissionSubmissionRestoresOnlyStillPendingRequests(bool appliedBeforeFailure, bool snapshots)
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var sessions = new DeferredTranscriptGateway(turnIsAvailable: true);
        var request = CreatePermissionRequest() with { SessionId = sessions.Turn.SessionId };
        FallbackPermissionGateway permissions = snapshots ? new SnapshotPermissionGateway(request) : new(request);
        var runs = new DeferredPermissionRunGateway(permissions) { FailNextMutation = true, ApplyBeforeFailure = appliedBeforeFailure };
        using var model = new AgentChatViewModel(new StaticProfileGateway(), new StaticWorkspaceGateway(),
            sessions, permissions, runs, dispatcher);
        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = model.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Task operation = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => operation = model.ApprovePermissionCommand.ExecuteAsync(request));
        runs.ReleaseMutation.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.InvokeAsync(() =>
        {
            if (appliedBeforeFailure) Assert.Empty(model.PendingPermissionRequests);
            else Assert.Equal(request, Assert.Single(model.PendingPermissionRequests));
            operation = model.DenyPermissionCommand.ExecuteAsync(request);
        });
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(appliedBeforeFailure ? 1 : 2, runs.MutationCount);
        Assert.Empty(model.PendingPermissionRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionCompletionCannotChangeTheNewSessionOrLoseFailedRequests(bool fail)
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var sessions = new DeferredTranscriptGateway(turnIsAvailable: true);
        var firstSession = sessions.ListSessions()[0];
        var otherId = Guid.NewGuid();
        sessions.OtherSession = firstSession with { SessionId = otherId, RootSessionId = otherId, Title = "Other session" };
        var request = CreatePermissionRequest() with { SessionId = firstSession.SessionId };
        var otherRequest = request with { SessionId = otherId, RequestId = "other-request" };
        var permissions = new FallbackPermissionGateway(request, otherRequest);
        var runs = new DeferredPermissionRunGateway(permissions) { FailNextMutation = fail };
        using var model = new AgentChatViewModel(new StaticProfileGateway(), new StaticWorkspaceGateway(),
            sessions, permissions, runs, dispatcher);
        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = model.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Task operation = Task.CompletedTask;
        string? otherStatus = null;
        await dispatcher.InvokeAsync(() =>
        {
            model.SelectedSession = model.Sessions.Single(session => session.SessionId == firstSession.SessionId);
            operation = model.DenyPermissionCommand.ExecuteAsync(request);
            model.SelectedSession = model.Sessions.Single(session => session.SessionId == otherId);
            otherStatus = model.StatusText;
        });
        runs.ReleaseMutation.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(otherId, model.SelectedSession?.SessionId);
            Assert.Equal(otherStatus, model.StatusText);
            Assert.Equal(otherRequest, Assert.Single(model.PendingPermissionRequests));
            model.SelectedSession = model.Sessions.Single(session => session.SessionId == firstSession.SessionId);
            if (fail) Assert.Equal(request, Assert.Single(model.PendingPermissionRequests));
            else Assert.Empty(model.PendingPermissionRequests);
        });
    }

    [Fact]
    public async Task PermissionSnapshotsKeepDismissedRequestsHiddenAndRetiredDecisionsCannotReleaseNewOnes()
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var request = CreatePermissionRequest();
        var other = request with { SessionId = Guid.NewGuid(), RequestId = "other-request" };
        var permissions = new FallbackPermissionGateway(request, other);
        var state = new AgentPermissionPanelState(permissions, NoOpRunGateway.Instance, dispatcher);
        await dispatcher.InvokeAsync(() =>
        {
            state.ApplySnapshot(request.SessionId, null, [request]);
            var decision = Assert.IsType<AgentPermissionPanelState.Decision>(state.TryBeginDecision(request));
            state.ApplySnapshot(request.SessionId, null, [request]);
            Assert.Empty(state.Requests);
            Assert.Null(state.TryBeginDecision(request));
            state.ApplySnapshot(other.SessionId, null, [other]);
            Assert.Equal(other, Assert.Single(state.Requests));
            state.ApplySnapshot(request.SessionId, null, [request]);
            Assert.Empty(state.Requests);
            state.AcceptDecision(decision);
            state.ApplySnapshot(request.SessionId, null, [request]);
            Assert.Empty(state.Requests);
            state.ApplySnapshot(request.SessionId, null, []);
            Assert.False(state.IsCurrentDecision(decision));
            state.ApplySnapshot(request.SessionId, null, [request]);
            var retired = Assert.IsType<AgentPermissionPanelState.Decision>(state.TryBeginDecision(request));
            state.ResetDecisions();
            state.ApplySnapshot(request.SessionId, null, [request]);
            var current = Assert.IsType<AgentPermissionPanelState.Decision>(state.TryBeginDecision(request));
            state.AcceptDecision(retired);
            state.ReleaseFailedDecision(retired);
            Assert.True(state.IsCurrentDecision(current));
            Assert.False(current.Accepted);
            state.ApplySnapshot(request.SessionId, null, [request]);
            Assert.Empty(state.Requests);
        });
    }

    private sealed class SnapshotPermissionGateway(params AgentPendingPermissionRequestRecord[] requests)
        : FallbackPermissionGateway(requests), IAgentChatPermissionCommandGateway
    {
        public Task<AgentChatPermissionProjection> LoadSessionPermissionsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentChatPermissionProjection(1, GetSessionState(sessionId), ListPendingRequestsForSessionTree(sessionId)));
        }

        public Task<AgentChatPermissionProjection> SetSessionUnrestrictedModeAsync(Guid sessionId, bool isEnabled, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

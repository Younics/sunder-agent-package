using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyChildRunCancellationAsync(ChildRunFixture fixture, AgentSessionService sessions, AgentProfileService profiles,
        AgentActiveRunRegistry activeRuns, ChildRunChatProvider provider, AgentChildRunRequest request)
    {
        var count = sessions.ListSessions().Count;
        var canceledProfile = ChildProfile(request.ChildProfile, "child.pre-canceled");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(request with { ChildProfile = canceledProfile }, canceled.Token));
            Assert.Equal(count, sessions.ListSessions().Count);
            Assert.Null(profiles.GetProfile(canceledProfile.ProfileId));
        }
        provider.BlockReadiness = true;
        using (var canceled = new CancellationTokenSource())
        {
            var pending = fixture.Run(request with { TaskId = "cancel-after-admission" }, canceled.Token);
            await provider.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var child = Assert.Single(sessions.ListSessions(), value => value.TaskId == "cancel-after-admission");
            Assert.Single(sessions.ListTurns(child.SessionId), value => value.Role == AgentMessageRole.User);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(sessions.GetSession(child.SessionId));
            Assert.True(profiles.GetProfile(request.ChildProfile.ProfileId)!.IsInternal);
            Assert.Equal(AgentRunStatus.Interrupted, sessions.GetLatestCheckpoint(child.SessionId)!.Status);
            Assert.False(activeRuns.IsActive(child.SessionId));
        }

        provider.BlockReadiness = false;
        provider.BlockStream = true;
        var partial = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTurn(Guid id, AgentTurnRecord turn)
        {
            if (turn.Role == AgentMessageRole.Assistant && turn.Items.Any(item => item.TextContent == "partial-child")) partial.TrySetResult(id);
        }
        sessions.TurnChanged += OnTurn;
        try
        {
            using var canceled = new CancellationTokenSource();
            var pending = fixture.Run(request with { TaskId = "cancel-during-stream" }, canceled.Token);
            var id = await partial.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled.Cancel();
            AgentChildRunResult? result = null;
            var failure = await Record.ExceptionAsync(async () => result = await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(failure is null or OperationCanceledException, failure?.ToString());
            if (result is not null) { Assert.Equal(id, result.SessionId); Assert.Equal(AgentRunStatus.Interrupted, result.Status); }
            Assert.Equal(AgentRunStatus.Interrupted, sessions.GetLatestCheckpoint(id)!.Status);
            var assistant = Assert.Single(sessions.ListTurns(id), turn => turn.Role == AgentMessageRole.Assistant);
            Assert.Equal("partial-child", Assert.Single(assistant.Items).TextContent);
            Assert.False(assistant.IsStreaming);
            Assert.False(activeRuns.IsActive(id));
            Assert.True(provider.Clients[^1].Disposed);
            Assert.True(provider.Clients[^1].EnumerationDisposed);
        }
        finally { sessions.TurnChanged -= OnTurn; provider.BlockStream = false; }

        provider.BlockReadiness = true;
        provider.ReadinessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = fixture.Run(request with { TaskId = "retired-activation" });
        await provider.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retiringChild = Assert.Single(sessions.ListSessions(), value => value.TaskId == "retired-activation");
        await fixture.State.RetireAsync(fixture.Implementation).WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => retired.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.Equal(AgentRunStatus.Interrupted, sessions.GetLatestCheckpoint(retiringChild.SessionId)!.Status);
        Assert.Single(sessions.ListTurns(retiringChild.SessionId), turn => turn.Role == AgentMessageRole.User);
        Assert.False(activeRuns.IsActive(retiringChild.SessionId));
    }
}

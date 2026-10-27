extern alias AgentApp;

using System.Reflection;
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using AgentApp::Sunder.Package.Agent.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentAdmissionReceiptTests
{
    [Fact]
    public async Task AdmissionReceiptIsBoundedAndKeepsPreparingActiveWithoutHistoryOrAdvancingReplay()
    {
        var owner = new AdmissionOwner();
        using var gateway = new AgentAppRuntimeGateway(owner);
        var session = new AgentSessionRecord(Guid.NewGuid(), "session", AgentSessionState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            { WorkspaceId = "workspace" };
        var cache = typeof(AgentAppRuntimeGateway).GetMethod("CacheSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        cache.Invoke(gateway, [new AgentSessionSnapshot(session, null)]);
        var first = Guid.NewGuid();
        for (var index = 0; index < 65; index++)
        {
            var id = index == 0 ? first : Guid.NewGuid();
            var checkpoint = await ((IAgentCorrelatedRunGateway)gateway).QueueUserMessageAsync(session.SessionId,
                "profile", "accepted", "workspace", [new("test.txt", "text/plain", [1])], id);
            Assert.Equal(AgentRunStatus.Idle, checkpoint.Status);
            Assert.Equal(id, gateway.GetTurn(id)!.TurnId);
            Assert.Equal(checkpoint, gateway.GetLatestCheckpoint(session.SessionId));
        }
        Assert.Null(gateway.GetTurn(first));
        Assert.Equal(0, owner.DiscardCount);
        Assert.Equal(0L, (long)typeof(AgentAppRuntimeGateway).GetField("_revision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!);
        cache.Invoke(gateway, [new AgentSessionSnapshot(session, null)]);
        Assert.Equal(65, gateway.GetLatestCheckpoint(session.SessionId)!.RunRevision);
        var terminal = new AgentRunCheckpointRecord(Guid.NewGuid(), session.SessionId, 65,
            AgentRunStatus.Completed, "finished after a clock correction", DateTimeOffset.UnixEpoch);
        cache.Invoke(gateway, [new AgentSessionSnapshot(session, terminal)]);
        Assert.Equal(terminal, gateway.GetLatestCheckpoint(session.SessionId));
    }

    private sealed class AdmissionOwner : OwnerServiceStub
    {
        private long _revision;
        internal int DiscardCount;
        public override ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(AgentAttachmentUploadRequest upload, CancellationToken token)
            => ValueTask.FromResult(new AgentAttachmentUploadHandle(Guid.NewGuid().ToString("N")));
        public override ValueTask DiscardAttachmentAsync(AgentAttachmentUploadHandle handle, CancellationToken token)
        {
            DiscardCount++;
            return ValueTask.CompletedTask;
        }
        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : class where TResponse : class
        {
            var command = Assert.IsType<AgentRunCommand>(request);
            var revision = ++_revision;
            var now = DateTimeOffset.UtcNow;
            var run = Guid.NewGuid();
            var turn = new AgentTurnRecord(command.UserTurnId!.Value, command.SessionId, AgentMessageRole.User,
                AgentTurnKind.Message, [], now, now) { RunId = run, RunRevision = revision, TurnSequence = revision };
            return ValueTask.FromResult((TResponse)(object)new AgentRunCommandResult(revision,
                new(Guid.NewGuid(), command.SessionId, revision, AgentRunStatus.Idle, "Preparing", now), run, turn.TurnId, turn));
        }
        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class where TEvent : class
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

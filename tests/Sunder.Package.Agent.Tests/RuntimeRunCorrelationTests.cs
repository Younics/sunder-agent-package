extern alias AgentCore;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class RuntimeRunCorrelationTests
{
    [Fact]
    public async Task AppRuntimeGateway_CorrelatedRunsCarryRequestedUserTurnIds()
    {
        var client = new CapturingRunRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        var correlatedGateway = (IAgentCorrelatedRunGateway)gateway;
        var sessionId = Guid.NewGuid();
        var startUserTurnId = Guid.NewGuid();
        var rollbackUserTurnId = Guid.NewGuid();
        var rollbackAnchorTurnId = Guid.NewGuid();

        await correlatedGateway.QueueUserMessageAsync(
            sessionId,
            "profile",
            "start",
            "workspace",
            [],
            startUserTurnId);
        await correlatedGateway.RollbackAndQueueUserMessageAsync(
            sessionId,
            rollbackAnchorTurnId,
            "profile",
            "rollback",
            "workspace",
            [],
            rollbackUserTurnId);
        var status = await gateway.GetRunCommandStatusAsync(sessionId, startUserTurnId);

        var commands = client.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(AgentRunCommandKind.Start, commands[0].Kind);
        Assert.Equal(startUserTurnId, commands[0].UserTurnId);
        Assert.Equal(AgentRunCommandKind.RollbackAndStart, commands[1].Kind);
        Assert.Equal(rollbackAnchorTurnId, commands[1].RollbackAnchorTurnId);
        Assert.Equal(rollbackUserTurnId, commands[1].UserTurnId);
        Assert.Equal(AgentRunCommandStatus.Pending, status);
        Assert.Equal(
            new AgentRunCommandStatusRequest(sessionId, startUserTurnId),
            client.LastStatusRequest);
    }

    [Fact]
    public async Task AppRuntimeGateway_UncorrelatedOverloadsGenerateStablePerCallUserTurnIds()
    {
        var client = new CapturingRunRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        var sessionId = Guid.NewGuid();

        await gateway.QueueUserMessageAsync(
            sessionId,
            "profile",
            "start",
            "workspace",
            []);
        await gateway.RollbackAndQueueUserMessageAsync(
            sessionId,
            Guid.NewGuid(),
            "profile",
            "replacement",
            "workspace",
            []);

        var commands = client.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.NotNull(commands[0].UserTurnId);
        Assert.NotNull(commands[1].UserTurnId);
        Assert.NotEqual(commands[0].UserTurnId, commands[1].UserTurnId);
    }

    private sealed class CapturingRunRuntimeClient : OwnerServiceStub
    {
        public bool IsAvailable => true;

        public ConcurrentQueue<AgentRunCommand> Commands { get; } = new();

        public AgentRunCommandStatusRequest? LastStatusRequest { get; private set; }

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentRunCommandStatusRequest)
            {
                LastStatusRequest = (AgentRunCommandStatusRequest)(object)request;
                return ValueTask.FromResult((TResponse)(object)new AgentRunCommandStatusResult(
                    1,
                    AgentRunCommandStatus.Pending));
            }
            if (request is not AgentRunCommand)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            var command = (AgentRunCommand)(object)request;
            Commands.Enqueue(command);
            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                command.SessionId,
                1,
                AgentRunStatus.Running,
                "Running.",
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult((TResponse)(object)new AgentRunCommandResult(1, checkpoint));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}

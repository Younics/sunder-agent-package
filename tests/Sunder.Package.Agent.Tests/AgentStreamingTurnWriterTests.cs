using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentStreamingTurnWriterTests
{
    [Fact]
    public async Task WriteAttemptAsync_PersistsFirstAndThrottledStreamingUpdates()
    {
        var host = new RecordingBehaviorLoopRuntime();
        var context = CreateContext();
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler());
        var state = writer.BeginCycle(
            host,
            context,
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());
        var chatClient = new CadenceChatClient(() => host.PersistedContents.Count);

        await writer.WriteAttemptAsync(
            state,
            chatClient,
            [],
            new ChatOptions(),
            CancellationToken.None);
        var result = writer.CompleteCycle(state);

        Assert.Equal(2, chatClient.PersistenceCountAfterImmediateThirdDelta);
        Assert.Equal(["first", "first second", "first second third"], host.PersistedContents);
        Assert.Equal("first second third", result.Text);
    }

    [Fact]
    public async Task ProviderCycleRunner_DisposesEachCycleClientAndRecreatesForNextCycle()
    {
        var firstClient = new DisposableChatClient("first");
        var secondClient = new DisposableChatClient("second");
        var host = new RecordingBehaviorLoopRuntime();
        host.ChatClients.Enqueue(firstClient);
        host.ChatClients.Enqueue(secondClient);
        var runner = new AgentProviderCycleRunner(
            new AgentStreamingTurnWriter(new AgentLoopTerminalHandler()));
        var context = CreateContext();
        var session = await runner.CreateSessionAsync(
            host,
            context,
            new AgentPromptPreparation([], [], false, null!, null!, null, 0),
            CancellationToken.None);

        await runner.RunCycleAsync(
            host,
            context,
            session,
            [],
            new AgentAssistantTurnState(),
            Stopwatch.StartNew(),
            CancellationToken.None);
        Assert.True(firstClient.IsDisposed);

        await runner.RunCycleAsync(
            host,
            context,
            session,
            [],
            new AgentAssistantTurnState(),
            Stopwatch.StartNew(),
            CancellationToken.None);
        Assert.True(secondClient.IsDisposed);
        Assert.Empty(host.ChatClients);
    }

    private static AgentBehaviorLoopContext CreateContext()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        return new AgentBehaviorLoopContext(
            new AgentSessionRecord(
                sessionId,
                "Session",
                AgentSessionState.Active,
                now,
                now),
            new AgentProfileRecord(
                "profile",
                "Profile",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now),
            "provider",
            "model",
            new AgentProviderRunCapabilities(
                SupportsNativeToolCalling: true,
                SupportsStreamingToolCalls: true,
                SupportsMultipleToolCalls: false,
                Summary: "Streaming test."),
            Workspace: null,
            ExecutionBinding: null,
            Guid.NewGuid(),
            RunRevision: 1,
            new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                sessionId,
                1,
                AgentRunStatus.Running,
                "Running.",
                now),
            now,
            "Test streaming.",
            Guid.NewGuid());
    }

    private sealed class CadenceChatClient(Func<int> getPersistenceCount) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("Cadence test");

        public int PersistenceCountAfterImmediateThirdDelta { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatMessage(ChatRole.Assistant, []);
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    response.Contents.Add(content);
                }
            }

            return new ChatResponse(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
            await Task.Delay(TimeSpan.FromMilliseconds(175), cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, " second");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " third");
            PersistenceCountAfterImmediateThirdDelta = getPersistenceCount();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this
                : serviceKey is null && serviceType == typeof(ChatClientMetadata) ? Metadata
                : null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingBehaviorLoopRuntime : IAgentBehaviorLoopRuntime
    {
        public List<string> PersistedContents { get; } = [];
        public Queue<IChatClient> ChatClients { get; } = new();

        public bool IsCurrentRun() => true;

        public IReadOnlyList<AgentTurnRecord> ListTurns() => [];

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => [];

        public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ChatClients.Dequeue());

        public AgentRunCheckpointRecord SaveCheckpoint(
            AgentRunStatus status,
            string? summary)
            => throw new NotSupportedException();

        public void LogEvent(
            PackageLogLevel level,
            string eventName,
            string message,
            long? elapsedMilliseconds = null,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null)
        {
        }

        public AgentTurnRecord UpsertAssistantTurn(
            AgentTurnRecord? assistantTurn,
            string content)
        {
            PersistedContents.Add(content);
            var now = DateTimeOffset.UtcNow;
            return new AgentTurnRecord(
                assistantTurn?.TurnId ?? Guid.NewGuid(),
                Guid.NewGuid(),
                AgentMessageRole.Assistant,
                AgentTurnKind.Message,
                [],
                assistantTurn?.CreatedAtUtc ?? now,
                now);
        }

        public ValueTask PublishLifecycleEventAsync(
            AgentLifecycleEventKind kind,
            AgentRunStatus status,
            AgentTurnRecord? triggerTurn = null,
            AgentRunCheckpointRecord? checkpoint = null,
            bool isInterrupted = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentToolCallOutcome> InvokeToolAsync(
            AgentToolCallRequest toolCall,
            AgentTurnRecord? assistantTurn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(
            IReadOnlyList<AgentToolCallRequest> toolCalls,
            AgentTurnRecord? assistantTurn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class DisposableChatClient(string text) : IChatClient
    {
        public bool IsDisposed { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("Disposable test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() => IsDisposed = true;
    }
}

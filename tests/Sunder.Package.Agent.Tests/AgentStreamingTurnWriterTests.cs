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
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WriteAttemptAsync_PersistsFirstAndThrottledStreamingUpdates()
    {
        var timeProvider = new ManualTimeProvider();
        var host = new RecordingBehaviorLoopRuntime();
        var context = CreateContext();
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler(), timeProvider);
        var state = writer.BeginCycle(
            host,
            context,
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());
        var chatClient = new CadenceChatClient(() => host.PersistedContents.Count);

        var writeTask = writer.WriteAttemptAsync(
            state,
            chatClient,
            [],
            new ChatOptions(),
            CancellationToken.None);
        await chatClient.FirstDeltaConsumed.WaitAsync(AsyncTimeout);
        Assert.Equal(["first"], host.PersistedContents);

        timeProvider.Advance(TimeSpan.FromMilliseconds(75));
        chatClient.ReleaseRemainingUpdates();
        await chatClient.ImmediateThirdDeltaConsumed.WaitAsync(AsyncTimeout);

        Assert.Equal(2, chatClient.PersistenceCountAfterImmediateThirdDelta);
        Assert.Equal(["first", "first second"], host.PersistedContents);

        chatClient.CompleteResponse();
        await writeTask.WaitAsync(AsyncTimeout);
        var result = writer.CompleteCycle(state);

        Assert.Equal(["first", "first second", "first second third"], host.PersistedContents);
        Assert.Equal("first second third", result.Text);
    }

    [Fact]
    public async Task WriteAttemptAsync_FlushesPendingTextWhileProviderIsPaused()
    {
        var timeProvider = new ManualTimeProvider();
        var host = new RecordingBehaviorLoopRuntime();
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler(), timeProvider);
        var state = writer.BeginCycle(
            host,
            CreateContext(),
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());
        var chatClient = new PausingChatClient();

        var writeTask = writer.WriteAttemptAsync(
            state,
            chatClient,
            [],
            new ChatOptions(),
            CancellationToken.None);
        await chatClient.UpdatesConsumed.WaitAsync(AsyncTimeout);

        Assert.Equal(["first"], host.PersistedContents);
        var timestampReadCount = timeProvider.TimestampReadCount;
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        timeProvider.FireTimers();
        await timeProvider.WaitForTimestampReadCountAsync(timestampReadCount + 2).WaitAsync(AsyncTimeout);

        Assert.Equal(["first", "first second"], host.PersistedContents);
        chatClient.Release();
        await writeTask.WaitAsync(AsyncTimeout);
        writer.CompleteCycle(state);
        Assert.Equal(["first", "first second"], host.PersistedContents);
    }

    [Fact]
    public async Task WriteAttemptAsync_PeriodicFlushHonorsIntervalFromLastPersistence()
    {
        var timeProvider = new ManualTimeProvider();
        var host = new RecordingBehaviorLoopRuntime();
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler(), timeProvider);
        var state = writer.BeginCycle(
            host,
            CreateContext(),
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());
        var chatClient = new CadenceChatClient(() => host.PersistedContents.Count);

        var writeTask = writer.WriteAttemptAsync(
            state,
            chatClient,
            [],
            new ChatOptions(),
            CancellationToken.None);
        await chatClient.FirstDeltaConsumed.WaitAsync(AsyncTimeout);
        timeProvider.Advance(TimeSpan.FromMilliseconds(75));
        chatClient.ReleaseRemainingUpdates();
        await chatClient.ImmediateThirdDeltaConsumed.WaitAsync(AsyncTimeout);

        Assert.Equal(["first", "first second"], host.PersistedContents);
        var timestampReadCount = timeProvider.TimestampReadCount;
        timeProvider.Advance(TimeSpan.FromMilliseconds(25));
        timeProvider.FireTimers();
        await timeProvider.WaitForTimestampReadCountAsync(timestampReadCount + 1).WaitAsync(AsyncTimeout);
        Assert.Equal(["first", "first second"], host.PersistedContents);

        // The second same-time tick is a barrier proving the first evaluation completed.
        timeProvider.FireTimers();
        await timeProvider.WaitForTimestampReadCountAsync(timestampReadCount + 2).WaitAsync(AsyncTimeout);
        Assert.Equal(["first", "first second"], host.PersistedContents);

        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        timeProvider.FireTimers();
        await timeProvider.WaitForTimestampReadCountAsync(timestampReadCount + 4).WaitAsync(AsyncTimeout);
        Assert.Equal(["first", "first second", "first second third"], host.PersistedContents);

        chatClient.CompleteResponse();
        await writeTask.WaitAsync(AsyncTimeout);
        writer.CompleteCycle(state);
        Assert.Equal(["first", "first second", "first second third"], host.PersistedContents);
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
            new AgentPromptPreparation([], [], false, null!, null!, null, [], 0),
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

    [Fact]
    public async Task WriteAttemptAsync_CapturesLatestUsageAndPrefersReportedTotal()
    {
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler());
        var state = writer.BeginCycle(
            new RecordingBehaviorLoopRuntime(),
            CreateContext(),
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());
        var chatClient = new UpdateChatClient(
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 100,
                    OutputTokenCount = 20,
                    CachedInputTokenCount = 80,
                    ReasoningTokenCount = 10,
                }),
            ]),
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails { TotalTokenCount = 150 }),
            ]));

        await writer.WriteAttemptAsync(state, chatClient, [], new ChatOptions(), CancellationToken.None);
        var result = writer.CompleteCycle(state);

        Assert.Equal(150, result.ReportedContextTokenCount);
    }

    [Fact]
    public async Task ResetForRetry_DiscardsUsageFromFailedAttempt()
    {
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler());
        var state = writer.BeginCycle(
            new RecordingBehaviorLoopRuntime(),
            CreateContext(),
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());

        await writer.WriteAttemptAsync(
            state,
            new UpdateChatClient(new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails { TotalTokenCount = 200 }),
            ])),
            [],
            new ChatOptions(),
            CancellationToken.None);
        writer.ResetForRetry(state);
        await writer.WriteAttemptAsync(
            state,
            new UpdateChatClient(new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 5,
                    CachedInputTokenCount = 8,
                    ReasoningTokenCount = 4,
                }),
            ])),
            [],
            new ChatOptions(),
            CancellationToken.None);

        Assert.Equal(15, writer.CompleteCycle(state).ReportedContextTokenCount);
    }

    [Fact]
    public async Task WriteAttemptAsync_InvalidTotalFallsBackToInputAndOutputUsage()
    {
        var writer = new AgentStreamingTurnWriter(new AgentLoopTerminalHandler());
        var state = writer.BeginCycle(
            new RecordingBehaviorLoopRuntime(),
            CreateContext(),
            new AgentAssistantTurnState(),
            Stopwatch.StartNew());
        var chatClient = new UpdateChatClient(new ChatResponseUpdate(ChatRole.Assistant,
        [
            new UsageContent(new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                TotalTokenCount = -1,
            }),
        ]));

        await writer.WriteAttemptAsync(state, chatClient, [], new ChatOptions(), CancellationToken.None);

        Assert.Equal(15, writer.CompleteCycle(state).ReportedContextTokenCount);
    }

    [Fact]
    public async Task ProviderCycleRunner_RetryDiscardsUsageFromFailedAttempt()
    {
        var chatClient = new RetryingUsageChatClient();
        var host = new RecordingBehaviorLoopRuntime();
        host.ChatClients.Enqueue(chatClient);
        var runner = new AgentProviderCycleRunner(
            new AgentStreamingTurnWriter(new AgentLoopTerminalHandler()));
        var context = CreateContext();
        var session = await runner.CreateSessionAsync(
            host,
            context,
            new AgentPromptPreparation([], [], false, null!, null!, null, [], 0),
            CancellationToken.None);

        var result = await runner.RunCycleAsync(
            host,
            context,
            session,
            [],
            new AgentAssistantTurnState(),
            Stopwatch.StartNew(),
            CancellationToken.None);

        Assert.Equal(2, chatClient.AttemptCount);
        Assert.Equal(15, result.ReportedContextTokenCount);
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
        private readonly TaskCompletionSource _completeResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstDeltaConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _immediateThirdDeltaConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRemainingUpdates = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatClientMetadata Metadata { get; } = new("Cadence test");

        public int PersistenceCountAfterImmediateThirdDelta { get; private set; }

        public Task FirstDeltaConsumed => _firstDeltaConsumed.Task;

        public Task ImmediateThirdDeltaConsumed => _immediateThirdDeltaConsumed.Task;

        public void ReleaseRemainingUpdates() => _releaseRemainingUpdates.TrySetResult();

        public void CompleteResponse() => _completeResponse.TrySetResult();

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
            _firstDeltaConsumed.TrySetResult();
            await _releaseRemainingUpdates.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, " second");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " third");
            PersistenceCountAfterImmediateThirdDelta = getPersistenceCount();
            _immediateThirdDeltaConsumed.TrySetResult();
            await _completeResponse.Task.WaitAsync(cancellationToken);
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

    private sealed class PausingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _updatesConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatClientMetadata Metadata { get; } = new("Pausing test");

        public Task UpdatesConsumed => _updatesConsumed.Task;

        public void Release() => _release.TrySetResult();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " second");
            _updatesConsumed.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
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

    private sealed class UpdateChatClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("Update test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class RetryingUsageChatClient : IChatClient
    {
        public int AttemptCount { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("Retrying usage test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = AttemptCount == 1 ? 150_000 : 10,
                    OutputTokenCount = AttemptCount == 1 ? 50_000 : 5,
                }),
            ]);
            await Task.Yield();
            if (AttemptCount == 1)
            {
                throw new AgentChatProviderException(
                    "The response ended prematurely. (ResponseEnded)",
                    "The response ended prematurely. (ResponseEnded)",
                    "ResponseEnded");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _syncRoot = new();
        private readonly HashSet<ManualTimer> _timers = [];
        private readonly List<TimestampWaiter> _timestampWaiters = [];
        private long _timestamp;
        private int _timestampReadCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public int TimestampReadCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _timestampReadCount;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_syncRoot)
            {
                return DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
            }
        }

        public override long GetTimestamp()
        {
            List<TaskCompletionSource>? completions = null;
            long timestamp;
            lock (_syncRoot)
            {
                timestamp = _timestamp;
                _timestampReadCount++;
                for (var index = _timestampWaiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _timestampWaiters[index];
                    if (waiter.TargetCount > _timestampReadCount)
                    {
                        continue;
                    }

                    completions ??= [];
                    completions.Add(waiter.Completion);
                    _timestampWaiters.RemoveAt(index);
                }
            }

            if (completions is not null)
            {
                foreach (var completion in completions)
                {
                    completion.TrySetResult();
                }
            }

            return timestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_syncRoot)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            lock (_syncRoot)
            {
                _timestamp = checked(_timestamp + elapsed.Ticks);
            }
        }

        public void FireTimers()
        {
            ManualTimer[] timers;
            lock (_syncRoot)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        public Task WaitForTimestampReadCountAsync(int targetCount)
        {
            lock (_syncRoot)
            {
                if (_timestampReadCount >= targetCount)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _timestampWaiters.Add(new TimestampWaiter(targetCount, completion));
                return completion.Task;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_syncRoot)
            {
                _timers.Remove(timer);
            }
        }

        private sealed record TimestampWaiter(int TargetCount, TaskCompletionSource Completion);

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private readonly object _syncRoot = new();
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_syncRoot)
                {
                    return !_disposed;
                }
            }

            public void Dispose()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }
                callback(state);
            }
        }
    }
}

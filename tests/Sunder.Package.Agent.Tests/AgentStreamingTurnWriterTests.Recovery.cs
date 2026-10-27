using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentStreamingTurnWriterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recovery_PublishesDeadlineAndStopOrReplacementPreventsAnotherRequest(bool partial, bool replaced)
    {
        var clock = new RecoveryClock();
        var client = new RecoveryClient(partial ? [new(ChatRole.Assistant, "saved partial")] : []);
        var host = new RecoveryHost();
        host.ChatClients.Enqueue(client);
        var runner = new AgentProviderCycleRunner(new AgentStreamingTurnWriter(new AgentLoopTerminalHandler()));
        var session = new AgentProviderSession(new ChatOptions(), "provider", "model");
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunCycleAsync(host, CreateContext(), session, [], new(), Stopwatch.StartNew(), cancellation.Token,
            retryState: new AgentProviderRetryState(clock, () => 0.5));
        await clock.Waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var retry = Assert.Single(host.Retries);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(partial, retry.IsContinuation);
        Assert.Equal(clock.GetUtcNow().AddSeconds(2), retry.RetryAtUtc);
        if (replaced)
        {
            host.Current = false;
            clock.Advance(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<AgentRunTranscriptWriteRejectedException>(() => run);
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, client.Attempts);
        Assert.True(client.Disposed);
        if (partial) Assert.Contains("saved partial", host.PersistedContents);
    }

    [Fact]
    public async Task Recovery_StopsAfterElevenAttemptsWithOneSharedStepAllowance()
    {
        var clock = new RecoveryClock();
        var client = new RecoveryClient([]);
        var host = new RecoveryHost();
        host.ChatClients.Enqueue(client);
        var runner = new AgentProviderCycleRunner(new AgentStreamingTurnWriter(new AgentLoopTerminalHandler()));
        var run = runner.RunCycleAsync(host, CreateContext(), new(new ChatOptions(), "provider", "model"), [], new(),
            Stopwatch.StartNew(), default, retryState: new AgentProviderRetryState(clock, () => 0.5));
        for (var index = 0; index < 10; index++)
        {
            var due = await clock.Waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(due);
        }
        var failure = await Assert.ThrowsAsync<AgentProviderRecoveryExhaustedException>(() => run);
        Assert.Contains("11 attempts", failure.Message);
        Assert.Equal(11, client.Attempts);
        Assert.Equal(10, host.Retries.Count);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task Recovery_PartialToolBatchIsNotReturnedForDispatch()
    {
        var clock = new RecoveryClock();
        var client = new RecoveryClient([new(ChatRole.Assistant,
            [new TextReasoningContent("Inspecting the request."), new FunctionCallContent("not-dispatched", "write", new Dictionary<string, object?>())])]);
        var host = new RecoveryHost();
        host.ChatClients.Enqueue(client);
        var runner = new AgentProviderCycleRunner(new AgentStreamingTurnWriter(new AgentLoopTerminalHandler()));
        var run = runner.RunCycleAsync(host, CreateContext(), new(new ChatOptions(), "provider", "model"), [], new(),
            Stopwatch.StartNew(), default, retryState: new AgentProviderRetryState(clock));
        clock.Advance(await clock.Waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<AgentProviderContinuationException>(() => run);
        Assert.Equal(1, client.Attempts);
        Assert.Contains(host.Activities, item => item.Kind == AgentRunActivityKind.Reasoning && item.Text.Contains("Inspecting"));
        Assert.True(Assert.Single(host.Retries).IsContinuation);
    }

    private sealed class RecoveryHost : RecordingBehaviorLoopRuntime, IAgentBehaviorLoopRuntime, IAgentProviderRetrySink, IAgentRunActivitySink
    {
        internal bool Current { get; set; } = true;
        bool IAgentBehaviorLoopRuntime.IsCurrentRun() => Current;
        internal List<AgentRunRetryState> Retries { get; } = [];
        internal List<(AgentRunActivityKind Kind, string Text)> Activities { get; } = [];
        public void ReportProviderRetry(AgentRunRetryState retry) => Retries.Add(retry);
        public void ReportRunActivity(AgentRunActivityKind kind, string text) => Activities.Add((kind, text));
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private readonly Sunder.Package.Agent.TestSupport.ManualTimeProvider _clock = new();
        internal Channel<TimeSpan> Waiting { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Waiting.Writer.TryWrite(dueTime);
            return timer;
        }
        internal void Advance(TimeSpan by) => _clock.Advance(by);
    }

    private sealed class RecoveryClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public int Attempts { get; private set; }
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Attempts++;
            await Task.Yield();
            foreach (var update in updates) yield return update;
            throw new AgentChatProviderException("overloaded", "Our servers are currently overloaded.");
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}

extern alias GeminiWorker;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using ProviderModels = GeminiWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using ProviderStream = GeminiWorker::Sunder.Package.Agent.Provider.Shared.ProviderChatStream;
using Xunit;

namespace Sunder.Package.Agent.Tests;

#pragma warning disable MEAI001

public sealed class AgentRpcStreamingBridgeTests
{
    [Fact]
    public async Task ProviderStream_CoalescesTinyReadyDeltasWithoutChangingAggregateContent()
    {
        var updates = Enumerable.Range(0, 256).Select(static _ => Update(new TextContent("x"))).ToArray();
        var chat = new SequenceChatClient(updates);
        var events = new List<ProviderModels.AgentChatStreamEvent>();
        var consumed = 0;
        await using var stream = ProviderEvents(chat).GetAsyncEnumerator();
        while (await stream.MoveNextAsync())
        {
            events.Add(stream.Current);
            consumed += Assert.IsType<ProviderModels.AgentChatUpdateEvent>(stream.Current).ContentItems!.Count;
            if (consumed < updates.Length)
            {
                // Coalescing drains ready values, not future producer work. Keep
                // a full16-value batch ready (one produced item may still be in
                // mapping/write), and wait for completion before the final tail.
                await chat.WaitForProducedAsync(Math.Min(updates.Length, consumed + 17),
                    requireCompletion: consumed + 17 >= updates.Length);
            }
        }

        var contents = events.SelectMany(item => Assert.IsType<ProviderModels.AgentChatUpdateEvent>(item).ContentItems!).ToArray();
        Assert.Equal(256, contents.Length);
        Assert.Equal(new string('x', 256), string.Concat(contents.Select(item => item.Text)));
        Assert.True(events.Count <= 32, $"Expected substantially fewer than 256 events, but observed {events.Count}.");
    }

    [Fact]
    public async Task ProviderStream_CoalescesOnlyMatchingTextAndReasoningEnvelopes()
    {
        var reasoning = new TextReasoningContent("reasoning") { ProtectedData = "protected" };
        var continuationUpdate = Update(new TextContent("f"), responseId: "response-2");
        continuationUpdate.ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 });
        var updates = new ChatResponseUpdate[]
        {
            Update(new TextContent("a"), responseId: "response-1"),
            Update(reasoning, responseId: "response-1"),
            Update(new TextContent("c"), responseId: "response-2"),
            Update(new FunctionCallContent("call-1", "tool", new Dictionary<string, object?>()), responseId: "response-2"),
            Update(new TextContent("d"), responseId: "response-2"),
            Update(new UsageContent(new UsageDetails { TotalTokenCount = 12 }), responseId: "response-2"),
            Update(new ErrorContent("content error") { ErrorCode = "content-error" }, responseId: "response-2"),
            Update(new TextContent("e"), responseId: "response-2"),
            new ChatResponseUpdate { ResponseId = "response-2", FinishReason = new ChatFinishReason("stop") },
            continuationUpdate,
        };
        var failure = new AgentChatProviderException("provider failed", "safe provider failure", "provider-error")
        { FailureKind = AgentChatProviderFailureKind.ContextWindowExceeded };

        var events = await ReadProviderEventsAsync(new SequenceChatClient(updates, failure));

        Assert.Equal(10, events.Count);
        var first = Assert.IsType<ProviderModels.AgentChatUpdateEvent>(events[0]);
        Assert.Equal(2, first.ContentItems!.Count);
        Assert.Equal("protected", first.ContentItems[1].ProtectedData);
        Assert.Equal("response-2", Assert.IsType<ProviderModels.AgentChatUpdateEvent>(events[1]).ResponseId);
        Assert.Equal(["text", "reasoning", "text", "tool-call", "text", "usage", "error", "text", "text"],
            events.OfType<ProviderModels.AgentChatUpdateEvent>().SelectMany(item => item.ContentItems ?? []).Select(item => item.Kind));
        Assert.Equal("stop", Assert.IsType<ProviderModels.AgentChatUpdateEvent>(events[7]).FinishReason);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<ProviderModels.AgentChatUpdateEvent>(events[8]).ContinuationHandle);
        var error = Assert.IsType<ProviderModels.AgentChatErrorEvent>(events[9]);
        Assert.Equal("provider-error", error.ErrorCode);
        Assert.Equal(ProviderModels.AgentChatProviderFailureKind.ContextWindowExceeded, error.ErrorFailureKind);
    }

    [Fact]
    public async Task ProviderStream_EarlyDisposalCancelsAndDrainsProducer()
    {
        var chat = new SequenceChatClient(Enumerable.Range(0, 256).Select(index => Update(new TextContent(index.ToString()))).ToArray());
        var enumerator = ProviderEvents(chat).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task ProviderStream_ProjectsNestedQueueFailureIntoTheNaturalErrorEvent()
    {
        var queueFailure = new SunderRpcException(new(SunderRpcErrorKind.ResourceExhausted,
            AgentChatFailureProjection.StreamQueueLimitErrorCode, "queue diagnostic",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var failure = new AgentChatProviderException("outer provider diagnostic", "outer provider content", "outer-provider-code", queueFailure);

        var item = Assert.IsType<ProviderModels.AgentChatErrorEvent>(Assert.Single(await ReadProviderEventsAsync(new SequenceChatClient([], failure))));

        Assert.Equal(AgentChatFailureProjection.StreamQueueLimitErrorCode, item.ErrorCode);
        var reconstructed = AgentChatFailureProjection.CreateProviderException(new AgentChatErrorEvent(
            item.ErrorCode, item.ErrorMessage, item.ErrorContent, AgentChatProviderFailureKind.Unknown));
        var projected = Assert.IsType<SunderRpcException>(reconstructed.InnerException);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, projected.Error.RetrySafety);
        Assert.True(AgentFailurePresentationPolicy.IsStreamQueueLimit(reconstructed));
        Assert.False(AgentProviderResilience.IsTransient(reconstructed));
    }

    [Fact]
    public async Task ProviderStream_ProjectsNestedUnsafeRpcMetadataIntoTheNaturalErrorEvent()
    {
        var unsafeFailure = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "rpc.worker.other-unsafe-failure",
            "connection failed", SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var failure = new AgentChatProviderException("outer provider diagnostic", "outer provider content", "outer-provider-code", unsafeFailure);

        var item = Assert.IsType<ProviderModels.AgentChatErrorEvent>(Assert.Single(await ReadProviderEventsAsync(new SequenceChatClient([], failure))));

        Assert.Equal(AgentChatFailureProjection.UnsafeRpcFailureErrorCode, item.ErrorCode);
        var reconstructed = AgentChatFailureProjection.CreateProviderException(new AgentChatErrorEvent(
            item.ErrorCode, item.ErrorMessage, item.ErrorContent, AgentChatProviderFailureKind.Unknown));
        var projected = Assert.IsType<SunderRpcException>(reconstructed.InnerException);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, projected.Error.RetrySafety);
        Assert.False(AgentProviderResilience.IsTransient(reconstructed));
    }

    [Fact]
    public async Task ProviderStream_CompletesReaderWhenClientDisposeThrows()
    {
        var chat = new SequenceChatClient([], disposeException: new InvalidOperationException("dispose failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadProviderEventsAsync(chat).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("dispose failed", exception.Message);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_SlowConsumerAdvancesProviderOneEventAtATimeAndDisposes()
    {
        var chat = new SequenceChatClient(Enumerable.Range(0, 256).Select(index => Update(new TextContent(index.ToString()))).ToArray());
        await using var catalog = new RegressionTestExtensionCatalog();
        using var grant = OpenRunControl(catalog, chat, out var invocationId);
        var control = Assert.Single((await catalog.RunControls.ListAsync()).Providers).Service;
        var enumerator = control.StreamChatAsync(invocationId, ChatRequest(), null).GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.IsType<AgentChatUpdateEvent>(enumerator.Current);
            Assert.Equal(1, chat.ProducedCount);
        }
        finally { await enumerator.DisposeAsync(); }

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_PreservesUpdatesThenMapsOneProviderError()
    {
        var failure = new AgentChatProviderException("provider failed", "safe failure", "expected-provider-error")
        { FailureKind = AgentChatProviderFailureKind.ContextWindowExceeded };
        var chat = new SequenceChatClient([Update(new TextContent("before failure"))], failure,
            new InvalidOperationException("cleanup must not replace provider failure"));
        await using var catalog = new RegressionTestExtensionCatalog();
        using var grant = OpenRunControl(catalog, chat, out var invocationId);
        var control = Assert.Single((await catalog.RunControls.ListAsync()).Providers).Service;
        var events = new List<AgentChatStreamEvent>();

        await foreach (var item in control.StreamChatAsync(invocationId, ChatRequest(), null)) events.Add(item);

        Assert.Equal(2, events.Count);
        Assert.IsType<AgentChatUpdateEvent>(events[0]);
        var error = Assert.IsType<AgentChatErrorEvent>(events[1]);
        Assert.Equal("expected-provider-error", error.ErrorCode);
        Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, error.ErrorFailureKind);
    }

    [Fact]
    public async Task RunControlStream_PropagatesNonProviderFailure()
    {
        var chat = new SequenceChatClient([Update(new TextContent("before failure"))],
            new InvalidOperationException("expected non-provider failure"),
            new InvalidOperationException("cleanup must not replace stream failure"));
        await using var catalog = new RegressionTestExtensionCatalog();
        using var grant = OpenRunControl(catalog, chat, out var invocationId);
        var control = Assert.Single((await catalog.RunControls.ListAsync()).Providers).Service;
        await using var enumerator = control.StreamChatAsync(invocationId, ChatRequest(), null).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => _ = await enumerator.MoveNextAsync());

        Assert.Equal("expected non-provider failure", exception.Message);
        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_CancellationPropagatesAndDisposesProviderEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var chat = new SequenceChatClient([Update(new TextContent("first")), Update(new TextContent("second"))],
            disposeException: new InvalidOperationException("cleanup must not replace cancellation"));
        await using var catalog = new RegressionTestExtensionCatalog();
        using var grant = OpenRunControl(catalog, chat, out var invocationId);
        var control = Assert.Single((await catalog.RunControls.ListAsync()).Providers).Service;
        var enumerator = control.StreamChatAsync(invocationId, ChatRequest(), null, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await enumerator.MoveNextAsync());
        }
        finally { await enumerator.DisposeAsync(); }

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_CancellationWinsOverConcurrentProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var chat = new SequenceChatClient([Update(new TextContent("first"))],
            new AgentChatProviderException("provider failed", "safe failure", "provider-error"));
        await using var catalog = new RegressionTestExtensionCatalog();
        using var grant = OpenRunControl(catalog, chat, out var invocationId);
        var control = Assert.Single((await catalog.RunControls.ListAsync()).Providers).Service;
        var enumerator = control.StreamChatAsync(invocationId, ChatRequest(), null, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await enumerator.MoveNextAsync());
        }
        finally { await enumerator.DisposeAsync(); }

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    private static IDisposable OpenRunControl(RegressionTestExtensionCatalog catalog, IChatClient chat, out Guid invocationId)
        => new AgentRoleRunControls(typeof(AgentActiveRunRegistry).Assembly, catalog.ActiveRuns)
            .Open(new ChatRuntime(chat), "sunder.package.agent", out invocationId);

    private static ChatResponseUpdate Update(AIContent content, string responseId = "response")
        => new(ChatRole.Assistant, [content]) { ResponseId = responseId };

    // Coalescing is the current provider-owned algorithm. Its generated natural
    // event values are asserted directly; run-control scenarios above cross RPC.
    private static IAsyncEnumerable<ProviderModels.AgentChatStreamEvent> ProviderEvents(IChatClient chat)
        => ProviderStream.ReadAsync((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return (ValueTask<IChatClient>)AgentRoleExceptionBoundary.Return(ValueTask.FromResult(chat),
                typeof(ValueTask<IChatClient>), typeof(ProviderStream).Assembly)!;
        }, new ProviderModels.AgentChatStreamRequest
        {
            ProviderId = "provider", ModelId = "model", Messages = [],
            Options = new()
            {
                Instructions = null, ConversationId = null, ModelId = null, Temperature = null,
                MaxOutputTokens = null, TopP = null, TopK = null, FrequencyPenalty = null,
                PresencePenalty = null, Seed = null, AllowMultipleToolCalls = null, ReasoningEffort = null,
                Tools = [], AdditionalProperties = null, ContinuationHandle = null,
            },
        }, CancellationToken.None);

    private static async Task<IReadOnlyList<ProviderModels.AgentChatStreamEvent>> ReadProviderEventsAsync(IChatClient chat)
    {
        var events = new List<ProviderModels.AgentChatStreamEvent>();
        await foreach (var item in ProviderEvents(chat)) events.Add(item);
        return events;
    }

    private static AgentChatStreamRequest ChatRequest()
        => new("provider", "model", [], new(null, null, null, null, null, null, null, null, null, null, null, null, [], null, null));

    private sealed class SequenceChatClient(IReadOnlyList<ChatResponseUpdate> updates,
        Exception? terminalException = null, Exception? disposeException = null) : IChatClient
    {
        private int _producedCount;
        private bool _enumerationDisposed;
        public int ProducedCount => Volatile.Read(ref _producedCount);
        public bool EnumerationDisposed => Volatile.Read(ref _enumerationDisposed);
        public bool IsDisposed { get; private set; }
        public async Task WaitForProducedAsync(int count, bool requireCompletion)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (ProducedCount < count || requireCompletion && !EnumerationDisposed)
                await Task.Delay(1, timeout.Token);
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            try
            {
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _producedCount);
                    yield return update;
                }
                if (terminalException is not null) throw terminalException;
            }
            finally { Volatile.Write(ref _enumerationDisposed, true); }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
            IsDisposed = true;
            if (disposeException is not null) throw disposeException;
        }
    }

    private sealed class ChatRuntime(IChatClient chatClient) : IAgentBehaviorLoopRuntime
    {
        public bool IsCurrentRun() => true;
        public IReadOnlyList<AgentTurnRecord> ListTurns() => [];
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => [];
        public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(chatClient);
        }
        public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary) => throw new NotSupportedException();
        public void LogEvent(PackageLogLevel level, string eventName, string message, long? elapsedMilliseconds = null, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null) { }
        public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content) => throw new NotSupportedException();
        public ValueTask PublishLifecycleEventAsync(AgentLifecycleEventKind kind, AgentRunStatus status, AgentTurnRecord? triggerTurn = null, AgentRunCheckpointRecord? checkpoint = null, bool isInterrupted = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AgentToolCallOutcome> InvokeToolAsync(AgentToolCallRequest toolCall, AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(IReadOnlyList<AgentToolCallRequest> toolCalls, AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

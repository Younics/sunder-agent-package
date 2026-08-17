using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

#pragma warning disable MEAI001

public sealed class AgentRpcStreamingBridgeTests
{
    [Fact]
    public async Task ProviderStream_CoalescesTinyReadyDeltasWithoutChangingAggregateContent()
    {
        var updates = Enumerable.Range(0, 256)
            .Select(static _ => Update(new TextContent("x")))
            .ToArray();
        var events = await ReadProviderEventsAsync(new SequenceChatClient(updates));

        var contents = events
            .SelectMany(static item => item.GetProperty("contentItems").EnumerateArray())
            .ToArray();
        Assert.Equal(256, contents.Length);
        Assert.Equal(new string('x', 256), string.Concat(contents.Select(static item => item.GetProperty("text").GetString())));
        Assert.True(events.Count <= 32, $"Expected substantially fewer than 256 wire events, but observed {events.Count}.");
    }

    [Fact]
    public async Task ProviderStream_CoalescesOnlyMatchingTextAndReasoningEnvelopes()
    {
        var reasoning = new TextReasoningContent("reasoning") { ProtectedData = "protected" };
        var continuation = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 });
        var continuationUpdate = Update(new TextContent("f"), responseId: "response-2");
        continuationUpdate.ContinuationToken = continuation;
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
        var providerFailure = new AgentChatProviderException(
            "provider failed",
            "safe provider failure",
            "provider-error")
        {
            FailureKind = AgentChatProviderFailureKind.ContextWindowExceeded,
        };

        var events = await ReadProviderEventsAsync(new SequenceChatClient(updates, providerFailure));

        Assert.Equal(10, events.Count);
        Assert.Equal(2, events[0].GetProperty("contentItems").GetArrayLength());
        Assert.Equal("protected", events[0].GetProperty("contentItems")[1].GetProperty("protectedData").GetString());
        Assert.Equal("response-2", events[1].GetProperty("responseId").GetString());
        string[] expectedContentKinds =
            ["text", "reasoning", "text", "tool-call", "text", "usage", "error", "text", "text"];
        var contentKinds = events
            .Where(static item => item.GetProperty("kind").GetString() == AgentChatStreamEvent.UpdateKind)
            .SelectMany(static item => item.TryGetProperty("contentItems", out var contentItems)
                ? contentItems.EnumerateArray().Select(static content => content.GetProperty("kind").GetString()!)
                : [])
            .ToArray();
        Assert.Equal(expectedContentKinds, contentKinds);
        Assert.Equal("stop", events[7].GetProperty("finishReason").GetString());
        Assert.Equal("AQID", events[8].GetProperty("continuationHandle").GetString());
        Assert.Equal(AgentChatStreamEvent.ErrorKind, events[9].GetProperty("kind").GetString());
        Assert.Equal("provider-error", events[9].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ProviderStream_EarlyDisposalCancelsAndDrainsProducer()
    {
        var chat = new SequenceChatClient(Enumerable.Range(0, 256)
            .Select(static index => Update(new TextContent(index.ToString())))
            .ToArray());
        using var requestDocument = JsonDocument.Parse(AgentRpcServiceHandler.Serialize(new AgentChatStreamRequest(
            "provider",
            "model",
            [],
            EmptyOptions())));
        var enumerator = AgentChatProviderRpc.CreateHandler(new ChatProvider(chat))
            .InvokeServerStreamAsync(
                null!,
                AgentChatProviderRpc.ServiceId,
                "stream-chat",
                requestDocument.RootElement.Clone())
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task ProviderStream_ProjectsNestedQueueFailureAcrossRpcErrorEvent()
    {
        var queueFailure = new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            AgentChatProviderRpc.StreamQueueLimitErrorCode,
            "queue diagnostic",
            SunderRpcDispatchState.PossiblyDispatched,
            SunderRpcRetrySafety.Unsafe));
        var providerFailure = new AgentChatProviderException(
            "outer provider diagnostic",
            "outer provider content",
            "outer-provider-code",
            queueFailure);

        var item = Assert.Single(await ReadProviderEventsAsync(
            new SequenceChatClient([], providerFailure)));
        var errorCode = item.GetProperty("errorCode").GetString();
        Assert.Equal(AgentChatProviderRpc.StreamQueueLimitErrorCode, errorCode);

        var reconstructed = AgentChatProviderRpc.CreateProviderException(new AgentChatErrorEvent(
            errorCode!,
            item.GetProperty("errorMessage").GetString()!,
            item.GetProperty("errorContent").GetString()!,
            AgentChatProviderFailureKind.Unknown));
        var projected = Assert.IsType<SunderRpcException>(reconstructed.InnerException);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, projected.Error.RetrySafety);
        Assert.True(AgentFailurePresentationPolicy.IsStreamQueueLimit(reconstructed));
        Assert.False(AgentProviderResilience.IsTransient(reconstructed));
    }

    [Fact]
    public async Task ProviderStream_ProjectsNestedUnsafeRpcMetadataAcrossErrorEvent()
    {
        var unsafeFailure = new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.worker.other-unsafe-failure",
            "connection failed",
            SunderRpcDispatchState.PossiblyDispatched,
            SunderRpcRetrySafety.Unsafe));
        var providerFailure = new AgentChatProviderException(
            "outer provider diagnostic",
            "outer provider content",
            "outer-provider-code",
            unsafeFailure);

        var item = Assert.Single(await ReadProviderEventsAsync(
            new SequenceChatClient([], providerFailure)));
        Assert.Equal(
            AgentChatProviderRpc.UnsafeRpcFailureErrorCode,
            item.GetProperty("errorCode").GetString());
        var reconstructed = AgentChatProviderRpc.CreateProviderException(new AgentChatErrorEvent(
            AgentChatProviderRpc.UnsafeRpcFailureErrorCode,
            "outer provider diagnostic",
            "outer provider content",
            AgentChatProviderFailureKind.Unknown));
        var projected = Assert.IsType<SunderRpcException>(reconstructed.InnerException);

        Assert.Equal(SunderRpcRetrySafety.Unsafe, projected.Error.RetrySafety);
        Assert.False(AgentProviderResilience.IsTransient(reconstructed));
    }

    [Fact]
    public async Task ProviderStream_CompletesReaderWhenClientDisposeThrows()
    {
        var chat = new SequenceChatClient(
            [],
            disposeException: new InvalidOperationException("dispose failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReadProviderEventsAsync(chat).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("dispose failed", exception.Message);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_SlowConsumerAdvancesProviderOneEventAtATimeAndDisposes()
    {
        var chat = new SequenceChatClient(Enumerable.Range(0, 256)
            .Select(static index => Update(new TextContent(index.ToString())))
            .ToArray());
        using var catalog = new RegressionTestExtensionCatalog();
        using var registration = catalog.RunControls.Open(
            new ChatRuntime(chat),
            "sunder.package.agent",
            out var invocationId);
        var reference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.RunControls));
        Assert.True(reference.TryAcquire(out var lease));
        using (lease)
        {
            var enumerator = lease.Service.StreamChatAsync(
                    null,
                    Request(invocationId),
                    CancellationToken.None)
                .GetAsyncEnumerator();
            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.IsType<AgentChatUpdateEvent>(enumerator.Current);
                Assert.Equal(1, chat.ProducedCount);
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_PreservesUpdatesThenMapsOneProviderError()
    {
        var failure = new AgentChatProviderException(
            "provider failed",
            "safe failure",
            "expected-provider-error")
        {
            FailureKind = AgentChatProviderFailureKind.ContextWindowExceeded,
        };
        var chat = new SequenceChatClient(
            [Update(new TextContent("before failure"))],
            failure,
            new InvalidOperationException("cleanup must not replace provider failure"));
        using var catalog = new RegressionTestExtensionCatalog();
        using var registration = catalog.RunControls.Open(
            new ChatRuntime(chat),
            "sunder.package.agent",
            out var invocationId);
        var reference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.RunControls));
        Assert.True(reference.TryAcquire(out var lease));
        using (lease)
        {
            var events = new List<AgentChatStreamEvent>();
            await foreach (var item in lease.Service.StreamChatAsync(
                               null,
                               Request(invocationId),
                               CancellationToken.None))
            {
                events.Add(item);
            }

            Assert.IsType<AgentChatUpdateEvent>(events[0]);
            var error = Assert.IsType<AgentChatErrorEvent>(events[1]);
            Assert.Equal("expected-provider-error", error.ErrorCode);
            Assert.Equal(AgentChatProviderFailureKind.ContextWindowExceeded, error.ErrorFailureKind);
            Assert.Equal(2, events.Count);
        }
    }

    [Fact]
    public async Task RunControlStream_PropagatesNonProviderFailure()
    {
        var chat = new SequenceChatClient(
            [Update(new TextContent("before failure"))],
            new InvalidOperationException("expected non-provider failure"),
            new InvalidOperationException("cleanup must not replace stream failure"));
        using var catalog = new RegressionTestExtensionCatalog();
        using var registration = catalog.RunControls.Open(
            new ChatRuntime(chat),
            "sunder.package.agent",
            out var invocationId);
        var reference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.RunControls));
        Assert.True(reference.TryAcquire(out var lease));
        using (lease)
        {
            var enumerator = lease.Service.StreamChatAsync(
                    null,
                    Request(invocationId),
                    CancellationToken.None)
                .GetAsyncEnumerator();
            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    _ = await enumerator.MoveNextAsync());
                Assert.Equal("expected non-provider failure", exception.Message);
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task RunControlStream_CancellationPropagatesAndDisposesProviderEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var chat = new SequenceChatClient([
            Update(new TextContent("first")),
            Update(new TextContent("second")),
        ], disposeException: new InvalidOperationException("cleanup must not replace cancellation"));
        using var catalog = new RegressionTestExtensionCatalog();
        using var registration = catalog.RunControls.Open(
            new ChatRuntime(chat),
            "sunder.package.agent",
            out var invocationId);
        var reference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.RunControls));
        Assert.True(reference.TryAcquire(out var lease));
        using (lease)
        {
            var enumerator = lease.Service.StreamChatAsync(
                    null,
                    Request(invocationId),
                    cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    _ = await enumerator.MoveNextAsync());
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    [Fact]
    public async Task RunControlStream_CancellationWinsOverConcurrentProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var chat = new SequenceChatClient(
            [Update(new TextContent("first"))],
            new AgentChatProviderException("provider failed", "safe failure", "provider-error"));
        using var catalog = new RegressionTestExtensionCatalog();
        using var registration = catalog.RunControls.Open(
            new ChatRuntime(chat),
            "sunder.package.agent",
            out var invocationId);
        var reference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.RunControls));
        Assert.True(reference.TryAcquire(out var lease));
        using (lease)
        {
            var enumerator = lease.Service.StreamChatAsync(
                    null,
                    Request(invocationId),
                    cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    _ = await enumerator.MoveNextAsync());
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        Assert.True(chat.EnumerationDisposed);
        Assert.True(chat.IsDisposed);
    }

    private static ChatResponseUpdate Update(AIContent content, string responseId = "response")
        => new(ChatRole.Assistant, [content]) { ResponseId = responseId };

    private static async Task<IReadOnlyList<JsonElement>> ReadProviderEventsAsync(IChatClient chatClient)
    {
        using var requestDocument = JsonDocument.Parse(AgentRpcServiceHandler.Serialize(new AgentChatStreamRequest(
            "provider",
            "model",
            [],
            EmptyOptions())));
        var request = requestDocument.RootElement.Clone();
        var events = new List<JsonElement>();
        await foreach (var item in AgentChatProviderRpc.CreateHandler(new ChatProvider(chatClient))
                           .InvokeServerStreamAsync(
                               null!,
                               AgentChatProviderRpc.ServiceId,
                               "stream-chat",
                               request))
        {
            events.Add(item.Clone());
        }
        return events;
    }

    private static RunControlChatRequest Request(Guid invocationId)
        => new(
            new RunControlInvocation(invocationId),
            new RunControlChatContext("provider", "model", null),
            [],
            EmptyOptions());

    private static AgentChatOptions EmptyOptions()
        => new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            null);

    private sealed class ChatProvider(IChatClient client) : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new(
            "provider",
            "Provider",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(client);
        }
    }

    private sealed class SequenceChatClient(
        IReadOnlyList<ChatResponseUpdate> updates,
        Exception? terminalException = null,
        Exception? disposeException = null) : IChatClient
    {
        public int ProducedCount { get; private set; }
        public bool EnumerationDisposed { get; private set; }
        public bool IsDisposed { get; private set; }

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
            await Task.CompletedTask;
            try
            {
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProducedCount++;
                    yield return update;
                }

                if (terminalException is not null)
                {
                    throw terminalException;
                }
            }
            finally
            {
                EnumerationDisposed = true;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            IsDisposed = true;
            if (disposeException is not null)
            {
                throw disposeException;
            }
        }
    }

    private sealed class ChatRuntime(IChatClient chatClient) : IAgentBehaviorLoopRuntime
    {
        public bool IsCurrentRun() => true;
        public IReadOnlyList<AgentTurnRecord> ListTurns() => [];
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => [];
        public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
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

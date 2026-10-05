using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

#pragma warning disable MEAI001

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyRunControlStreamsAsync(Assembly assembly, RunControlFixture fixture)
    {
        var runtime = new RunControlProbe();
        using var lease = fixture.Registry.Open(runtime, RunControlCaller, out var invocationId);
        var invocation = new RunControlInvocation(invocationId);
        var update = new ChatResponseUpdate(ChatRole.Assistant,
        [
            new TextContent("Hello 漢字"), new TextReasoningContent("Reasoning") { ProtectedData = "protected-state" },
            new FunctionCallContent("call.one", "lookup", new Dictionary<string, object?> { ["optional"] = null, ["flag"] = false }),
            new FunctionResultContent("call.one", null), new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 0 }),
            new ErrorContent("Tool error") { ErrorCode = "tool.failed", Details = "Detail" },
            new DataContent("data:image/png;base64,AAEC", "image/png") { Name = "image.png" },
        ])
        {
            ResponseId = "response.one", MessageId = "message.one", ConversationId = "conversation.one", ModelId = "model.one",
            CreatedAt = runtime.Context.RunStartedAtUtc, FinishReason = ChatFinishReason.Stop,
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
        };
        var chats = new List<RunControlChatProbe>();
        runtime.ChatFactory = () => { var value = new RunControlChatProbe([update]); chats.Add(value); return value; };
        var request = RunChatRequest(invocation) with
        {
            Messages = [new("user", "message.in", "Author", [new("text", Text: "Input 漢字"), new("tool-result", CallId: "call.in", Json: new(RunJson((object?)null)))])],
            Context = new("provider.one", "model.one", new Dictionary<string, JsonElement> { ["nested"] = RunJson(new { flag = false }) }),
            Options = new("Instructions", "conversation.in", "model.one", 0.5f, 99, 0.75f, 2, 0f, -0.5f, 0, false, "extra-high",
                [new("lookup", "Tool", RunJson(new { type = "object" }))],
                new Dictionary<string, JsonElement> { ["null"] = RunJson((object?)null), ["values"] = RunJson(new object[] { 1, false, "漢字" }) }, [3, 2, 1]),
        };
        var json = RunJson(request);
        var expected = new List<JsonElement> { RunJson<AgentChatStreamEvent>(AgentChatWireMapper.FromUpdate(update)) };
        var actual = await ReadGeneratedStream(fixture.Client, default, null, "StreamChatAsync", json);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++) Assert.True(JsonElement.DeepEquals(expected[index], actual[index]));
        Assert.Equal("AQID", actual[0].GetProperty("continuationHandle").GetString());
        Assert.Equal(JsonValueKind.Null, actual[0].GetProperty("contentItems")[3].GetProperty("json").ValueKind);
        Assert.Single(chats);
        Assert.All(chats, chat => { Assert.True(chat.Disposed); Assert.True(chat.EnumerationDisposed); });
        Assert.True(JsonElement.DeepEquals(RunJson(AgentChatWireMapper.FromChatOptions(AgentChatWireMapper.ToChatOptions(request.Options))),
            RunJson(AgentChatWireMapper.FromChatOptions(chats[0].Options))));
        Assert.True(JsonElement.DeepEquals(RunJson(request.Messages), RunJson(chats[0].Messages.Select(AgentChatWireMapper.FromChatMessage).ToArray())));
        var options = Assert.IsType<ChatOptions>(chats[0].Options);
        Assert.False(options.AllowMultipleToolCalls);
        Assert.Equal(ReasoningEffort.ExtraHigh, options.Reasoning!.Effort);
        Assert.Equal(new byte[] { 3, 2, 1 }, options.ContinuationToken!.ToBytes().ToArray());

        var observed = new BehaviorCallRecorder(fixture.Rpc);
        using var callbackCatalog = new AgentRpcCatalog(observed);
        var callback = (IAgentBehaviorLoopRuntime)Activator.CreateInstance(
            assembly.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedBehaviorRuntime", true)!, callbackCatalog, invocationId,
            AgentRpcProviderHandle.From(Assert.Single(fixture.State.Discover(AgentRpcContractIds.RunControl).Providers, provider => provider.Endpoint == fixture.Source)))!;
        using var callbackChat = await callback.CreateChatClientAsync(new(request.Context.ProviderId, request.Context.ModelId,
            CorrelationAttributes: request.Context.CorrelationAttributes!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)));
        var callbackEvents = new List<JsonElement>();
        await foreach (var item in callbackChat.GetStreamingResponseAsync(request.Messages.Select(message => AgentChatWireMapper.ToChatMessage(message)),
            AgentChatWireMapper.ToChatOptions(request.Options)))
            callbackEvents.Add(RunJson<AgentChatStreamEvent>(AgentChatWireMapper.FromUpdate(item)));
        Assert.Equal(expected.Count, callbackEvents.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var expectedUpdate = AgentChatWireMapper.ToUpdate(Assert.IsType<AgentChatUpdateEvent>(expected[index].Deserialize<AgentChatStreamEvent>(ExecutionJson)));
            Assert.True(JsonElement.DeepEquals(RunJson<AgentChatStreamEvent>(AgentChatWireMapper.FromUpdate(expectedUpdate)), callbackEvents[index]));
        }
        Assert.True(JsonElement.DeepEquals(RunJson(AgentChatWireMapper.FromChatOptions(chats[0].Options)), RunJson(AgentChatWireMapper.FromChatOptions(chats[^1].Options))));
        Assert.True(Assert.Single(observed.Scopes).Disposed);

        // Single-use binary content referenced twice is opened once and reaches both messages.
        var bytes = new byte[] { 0, 1, 127, 128, 255 };
        await foreach (var _ in callbackChat.GetStreamingResponseAsync([new(ChatRole.User,
            [new DataContent(bytes, "image/png") { Name = "callback.png" }])])) { }
        Assert.Equal(bytes, Assert.IsType<DataContent>(Assert.Single(chats[^1].Messages).Contents[0]).Data.ToArray());
        Assert.Equal("callback.png", Assert.IsType<DataContent>(chats[^1].Messages[0].Contents[0]).Name);
        Assert.Equal(2, observed.Scopes.Count); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
        await using var content = new MemoryStream(bytes, writable: false);
        var reference = await fixture.Scope.RegisterContentAsync(fixture.Source, content,
            new("image/png", "image.png", bytes.Length, Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1));
        var binary = RunJson(request with { Messages = [new("user", null, null,
            [new("content-reference", ContentReference: reference), new("content-reference", ContentReference: reference)])] });
        await ReadGeneratedStream(fixture.Client, default, null, "StreamChatAsync", binary);
        Assert.All(chats[^1].Messages.Single().Contents, item => Assert.Equal(bytes, Assert.IsType<DataContent>(item).Data.ToArray()));
        await Assert.ThrowsAsync<SunderRpcException>(() => ReadGeneratedStream(fixture.Client, default, null, "StreamChatAsync", binary));
        Assert.True(chats[^1].Disposed);

        var simple = RunJson(RunChatRequest(invocation));
        var slow = new RunControlChatProbe(Enumerable.Repeat(update, 256).ToArray());
        runtime.ChatFactory = () => slow;
        await using (var events = callbackChat.GetStreamingResponseAsync([]).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, slow.Produced);
        }
        Assert.True(slow.Disposed);
        Assert.True(slow.EnumerationDisposed);

        foreach (var stage in new[] { "create", "stream", "dispose" })
        {
            var providerFailure = new AgentChatProviderException("Provider failed", "Safe content", "context-limit") { FailureKind = AgentChatProviderFailureKind.ContextWindowExceeded };
            var chat = new RunControlChatProbe([update])
            {
                Failure = stage == "stream" ? providerFailure : null,
                DisposeFailure = stage == "dispose" ? providerFailure : new IOException("private cleanup detail"),
            };
            runtime.ChatFactory = () => stage == "create" ? throw providerFailure : chat;
            var events = await ReadGeneratedStream(fixture.Client, default, null, "StreamChatAsync", simple);
            Assert.Equal(stage == "create" ? 1 : 2, events.Count);
            var error = events[^1];
            Assert.Equal("error", error.GetProperty("kind").GetString());
            Assert.Equal("context-limit", error.GetProperty("errorCode").GetString());
            Assert.Equal("context-window-exceeded", error.GetProperty("errorFailureKind").GetString());
            Assert.Equal("Safe content", error.GetProperty("errorContent").GetString());
            if (stage != "create") { Assert.True(chat.Disposed); Assert.True(chat.EnumerationDisposed); }
        }
        foreach (var stage in new[] { "create", "stream", "dispose" })
        {
            var underlying = new IOException("private primary failure");
            var chat = new RunControlChatProbe([update]) { Failure = stage == "stream" ? underlying : null, DisposeFailure = stage == "dispose" ? underlying : new IOException("private cleanup detail") };
            runtime.ChatFactory = () => stage == "create" ? throw underlying : chat;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => ReadGeneratedStream(fixture.Client, default, null, "StreamChatAsync", simple));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
            Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
            Assert.Same(underlying, fixture.State.LastInvocationFailure);
            if (stage != "create") { Assert.True(chat.Disposed); Assert.True(chat.EnumerationDisposed); }
        }
        foreach (var blocked in new[] { false, true })
        {
            var chat = new RunControlChatProbe([update])
            {
                BlockAfterUpdate = blocked,
                Failure = new AgentChatProviderException("Concurrent provider failure", "Safe", "provider.error"),
                DisposeFailure = new IOException("private cancellation cleanup"),
            };
            runtime.ChatFactory = () => chat;
            using var cancellation = new CancellationTokenSource();
            await using (var events = RunControlEvents(fixture.Client, simple, cancellation.Token).GetAsyncEnumerator())
            {
                Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                var pending = blocked ? events.MoveNextAsync().AsTask() : null;
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (pending ?? events.MoveNextAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.True(chat.Disposed);
            Assert.True(chat.EnumerationDisposed);
        }
    }

    private static RunControlChatRequest RunChatRequest(RunControlInvocation invocation)
        => new(invocation, new("provider.one", "model.one", null), [],
            new(null, null, null, null, null, null, null, null, null, null, null, null, [], null, null));

    private static IAsyncEnumerable<JsonElement> RunControlEvents(object client, JsonElement request, CancellationToken token)
    {
        var method = client.GetType().GetMethod("StreamChatAsync")!;
        var value = JsonSerializer.Deserialize(request.GetRawText(), method.GetParameters()[0].ParameterType, ExecutionJson);
        var stream = method.Invoke(client, [value, token])!;
        return (IAsyncEnumerable<JsonElement>)typeof(AgentPublicSourceMigrationTests).GetMethod(nameof(ProjectGeneratedEvents), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [stream, token])!;
    }

    private sealed class RunControlChatProbe(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public ChatMessage[] Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }
        public int Produced { get; private set; }
        public bool Disposed { get; private set; }
        public bool EnumerationDisposed { get; private set; }
        public bool BlockAfterUpdate { get; init; }
        public Exception? Failure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Messages = messages.ToArray(); Options = options;
            await Task.CompletedTask;
            try
            {
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Produced++;
                    yield return update;
                    if (BlockAfterUpdate) await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (Failure is not null) throw Failure;
            }
            finally { EnumerationDisposed = true; }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { Disposed = true; if (DisposeFailure is not null) throw DisposeFailure; }
    }
}

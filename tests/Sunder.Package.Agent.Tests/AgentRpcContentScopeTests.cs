using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcContentScopeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ChatClient_UsesCallerScopeForBinaryContentAndDisposesIt()
    {
        var client = new ScopeCreatingRpcClient();
        var endpoint = new SunderRpcEndpointReference("rpc1_chat_provider");
        var provider = new AgentChatProviderRpcClient(client, endpoint);
        using var chat = await provider.CreateChatClientAsync(new AgentChatClientContext("provider", "model"));
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in chat.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "image.png" }])]))
        {
            updates.Add(update);
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, client.Scope.RegisteredContent);
        Assert.Equal(endpoint, client.Scope.RegisteredEndpoint);
        Assert.True(client.Scope.Disposed);
        Assert.False(client.DirectSubscriptionUsed);
        var text = Assert.IsType<TextContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Equal("received", text.Text);
    }

    [Fact]
    public async Task ChatClient_DoesNotCreateScopeForTextOnlyContent()
    {
        var client = new ScopeCreatingRpcClient();
        var provider = new AgentChatProviderRpcClient(
            client,
            new SunderRpcEndpointReference("rpc1_chat_provider"));
        using var chat = await provider.CreateChatClientAsync(new AgentChatClientContext("provider", "model"));

        await foreach (var _ in chat.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hello")]))
        {
        }

        Assert.Equal(0, client.ScopeCreationCount);
        Assert.True(client.DirectSubscriptionUsed);
        Assert.False(client.Scope.Disposed);
    }

    private sealed class ScopeCreatingRpcClient : ISunderRpcClient
    {
        public RecordingCallScope Scope { get; } = new();
        public int ScopeCreationCount { get; private set; }
        public bool DirectSubscriptionUsed { get; private set; }

        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScopeCreationCount++;
            return ValueTask.FromResult<ISunderRpcCallScope>(Scope);
        }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            DirectSubscriptionUsed = true;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ResponseEvent();
        }
    }

    private sealed class RecordingCallScope : ISunderRpcCallScope
    {
        public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public byte[]? RegisteredContent { get; private set; }
        public SunderRpcEndpointReference? RegisteredEndpoint { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISunderRpcCallScope>(this);

        public async ValueTask<SunderRpcContentReference> RegisterContentAsync(
            SunderRpcEndpointReference endpoint,
            Stream source,
            SunderRpcContentRegistrationOptions options,
            CancellationToken cancellationToken = default)
        {
            await using var destination = new MemoryStream();
            await source.CopyToAsync(destination, cancellationToken);
            RegisteredContent = destination.ToArray();
            RegisteredEndpoint = endpoint;
            return new SunderRpcContentReference(
                "rpc-content-test",
                RegisteredContent.Length,
                Convert.ToHexString(SHA256.HashData(RegisteredContent)).ToLowerInvariant(),
                options.MediaType,
                options.FileName,
                DeadlineUtc,
                SunderRpcContentRepeatability.SingleUse);
        }

        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
            SunderRpcEndpointReference endpoint,
            string filePath,
            SunderRpcContentRegistrationOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<Stream> OpenContentAsync(
            SunderRpcContentReference reference,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Equal(AgentChatProviderRpc.ServiceId, serviceId);
            Assert.Equal("stream-chat", methodId);
            Assert.Equal("rpc-content-test", request
                .GetProperty("messages")[0]
                .GetProperty("contents")[0]
                .GetProperty("contentReference")
                .GetProperty("id")
                .GetString());
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ResponseEvent();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static JsonElement ResponseEvent()
        => JsonSerializer.SerializeToElement(
            new AgentChatStreamEvent(
                AgentChatStreamEvent.UpdateKind,
                ContentItems: [new AgentChatContent("text", Text: "received")]),
            JsonOptions);
}

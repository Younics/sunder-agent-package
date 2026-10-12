using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcContentScopeTests
{
    [Fact]
    public async Task ChatClient_UsesCallerScopeForBinaryContentAndDisposesIt()
    {
        var client = new ScopeCreatingRpcClient();
        var endpoint = client.Scope.Provider.Endpoint;
        await using var receiver = new GeneratedServiceTestScope(client);
        var provider = Assert.Single((await receiver.Providers<AgentServices.IChatProviderService>().ListAsync()).Providers);
        using var chat = new AgentModelChatClient(new AgentChatClientContext("provider", "model"), provider.Service.StreamChatAsync);
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
    public async Task ChatClient_TextOnlyStreamDisposesScopeWithoutRegisteringBinaryContent()
    {
        var client = new ScopeCreatingRpcClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var provider = Assert.Single((await receiver.Providers<AgentServices.IChatProviderService>().ListAsync()).Providers);
        using var chat = new AgentModelChatClient(new AgentChatClientContext("provider", "model"), provider.Service.StreamChatAsync);

        await foreach (var _ in chat.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hello")]))
        {
        }

        Assert.Equal(1, client.ScopeCreationCount);
        Assert.Null(client.Scope.RegisteredContent);
        Assert.False(client.DirectSubscriptionUsed);
        Assert.True(client.Scope.Disposed);
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SunderRpcProviderSnapshot?>(endpoint == Scope.Provider.Endpoint ? Scope.Provider : null);
        }

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 7,
                string.IsNullOrEmpty(contractId) || contractId == Scope.Provider.ContractId ? [Scope.Provider] : []));
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, afterRevision);
            Assert.Equal(7, afterSequence);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

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
            yield return await ResponseEventAsync(Scope.Provider, cancellationToken);
        }
    }

    private sealed class RecordingCallScope : ISunderRpcCallScope
    {
        public SunderRpcProviderSnapshot Provider { get; } = CreateProvider();
        public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public byte[]? RegisteredContent { get; private set; }
        public SunderRpcEndpointReference? RegisteredEndpoint { get; private set; }
        public bool Disposed { get; private set; }

        private static SunderRpcProviderSnapshot CreateProvider()
        {
            var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ChatProviders);
            return new("test.package", "2.0.0", "test.provider", descriptor.ContractId, descriptor.Version,
                descriptor.Sha256, descriptor.AbiSha256!, Guid.NewGuid(), 1, 1, new("rpc1_chat_provider"), 1, SunderRpcProviderState.Active);
        }

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
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(Provider);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
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
            Assert.Equal("chat-provider", serviceId);
            Assert.Equal("stream-chat", methodId);
            var content = request.GetProperty("messages")[0].GetProperty("contents")[0];
            if (RegisteredContent is not null)
            {
                Assert.Equal("rpc-content-test", content.GetProperty("content").GetProperty("id").GetString());
            }
            else
            {
                Assert.Equal("hello", content.GetProperty("text").GetString());
                Assert.True(!content.TryGetProperty("content", out var binary) || binary.ValueKind == JsonValueKind.Null);
            }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return await ResponseEventAsync(Provider, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static async ValueTask<JsonElement> ResponseEventAsync(SunderRpcProviderSnapshot provider, CancellationToken cancellationToken)
    {
        var invocation = new SunderRpcInvocationContext("sunder.package.agent", "2.0.0", provider,
            DateTimeOffset.UtcNow.AddMinutes(1), 1, cancellationToken);
        await using var response = SunderRpcMarshallingContext.ForInvocation(invocation);
        try
        {
            // Even a text-only event contains resource-bearing model types. Use
            // an explicit SDK resource context instead of unbound JSON options.
            return await response.SerializeAsync<AgentChatStreamEvent>(
                new AgentChatUpdateEvent(ContentItems: [new AgentChatContent("text", Text: "received")]),
                cancellationToken: cancellationToken);
        }
        finally { invocation.Revoke(); }
    }
}

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Chat_Provider;
using Wire = Sunder.Generated.Rpc.ChatProvider;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedChatProviderClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentChatProvider, IAgentUtilityModelProvider, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.chat.provider-stale",
        "The selected chat provider activation is no longer current.");
    private AgentProviderDescriptor? _descriptor;

    public AgentProviderDescriptor Descriptor
    {
        get
        {
            _calls.ThrowIfStopped();
            return _descriptor ??= Invoke(async (client, token) => AgentConsumerValues.Convert<AgentProviderDescriptor>(
                await client.DescribeAsync(new(), token).ConfigureAwait(false)), default).AsTask().GetAwaiter().GetResult();
        }
    }

    public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => Invoke<IReadOnlyList<AgentModelDescriptor>>(async (client, token) => AgentConsumerValues.Convert<AgentModelDescriptor[]>(
            await client.ListModelsAsync(new(), token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentConsumerValues.Convert<AgentProviderReadiness>(
            await client.GetReadinessAsync(new(), token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => AgentConsumerValues.Convert<AgentProviderRunCapabilities>(
            await client.GetCapabilitiesAsync(new() { ModelId = modelId }, token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default)
        => Invoke((client, token) => client.ResolveUtilityModelAsync(new(), token), cancellationToken);

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.ThrowIfStopped();
        return ValueTask.FromResult<IChatClient>(new ChatClient(this, context));
    }

    private ValueTask<T> Invoke<T>(Func<Wire.ChatProviderClient, CancellationToken, ValueTask<T>> invoke, CancellationToken token)
        => _calls.Invoke((scope, cancellation) => invoke(Client(scope), cancellation), token);

    private Wire.ChatProviderClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).ChatProvider.CreateClient(scope, new Api.Provider(provider)).SChat_2dprovider;

    private async IAsyncEnumerable<ChatResponseUpdate> Stream(ISunderRpcCallScope scope, AgentChatClientContext context,
        IEnumerable<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken token)
    {
        // AI content/options mapping is package semantics. The generated client owns
        // wire validation/dispatch; one call scope owns attachments and the stream.
        var mapped = await AgentChatWireMapper.FromChatMessagesAsync(messages,
            (content, cancellation) => AgentChatWireMapper.RegisterContentAsync(scope, provider.Endpoint, content, cancellation), token).ConfigureAwait(false);
        var request = AgentConsumerValues.Convert<Wire.AgentChatStreamRequest>(new AgentChatStreamRequest(
            context.ProviderId, context.ModelId, mapped, AgentChatWireMapper.FromChatOptions(options)));
        await foreach (var item in Client(scope).StreamChatAsync(request, token).WithCancellation(token).ConfigureAwait(false))
        {
            var value = AgentConsumerValues.Convert<Wire.AgentChatStreamEvent, AgentChatStreamEvent>(item);
            if (value is AgentChatErrorEvent error) throw AgentChatFailureProjection.CreateProviderException(error);
            yield return AgentChatWireMapper.ToUpdate((AgentChatUpdateEvent)value);
        }
    }

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();

    private sealed class ChatClient(GeneratedChatProviderClient owner, AgentChatClientContext context) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => owner._calls.Subscribe((scope, token) => owner.Stream(scope, context, messages, options, token), cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        // The provider reference owns the shared binding, not this borrowed model
        // facade. Each enumeration owns/disposes its call scope independently.
        public void Dispose() { }
    }
}

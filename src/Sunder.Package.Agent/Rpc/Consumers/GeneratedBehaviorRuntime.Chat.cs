using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.RunControl;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed partial class GeneratedBehaviorRuntime
{
    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Wire.RunControlChatContext
        {
            ProviderId = context.ProviderId, ModelId = context.ModelId,
            CorrelationAttributes = context.CorrelationAttributes?.ToDictionary(pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal),
        };
        return ValueTask.FromResult<IChatClient>(new ChatClient(this, request));
    }

    private async IAsyncEnumerable<ChatResponseUpdate> Stream(Wire.RunControlChatContext context, IEnumerable<ChatMessage> messages,
        ChatOptions? options, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_control.TryAcquire(out var lease)) throw Stale();
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.RetirementToken))
            await foreach (var item in lease.Service.Subscribe(Read, invocation.Token).WithCancellation(invocation.Token).ConfigureAwait(false)) yield return item;

        async IAsyncEnumerable<ChatResponseUpdate> Read(ISunderRpcCallScope scope, Wire.RunControlClient client, [EnumeratorCancellation] CancellationToken cancellation)
        {
            var mapped = await AgentChatWireMapper.FromChatMessagesAsync(messages,
                (content, stopped) => AgentChatWireMapper.RegisterContentAsync(scope, lease.Service.Endpoint, content, stopped), cancellation).ConfigureAwait(false);
            var request = new Wire.RunControlChatRequest
            {
                Invocation = Invocation, Context = context, Messages = Map<Wire.AgentChatMessage[]>(mapped),
                Options = Map<Wire.AgentChatOptions>(AgentChatWireMapper.FromChatOptions(options)),
            };
            await foreach (var item in client.StreamChatAsync(request, cancellation).WithCancellation(cancellation).ConfigureAwait(false))
            {
                var value = AgentConsumerValues.Convert<Wire.AgentChatStreamEvent, AgentChatStreamEvent>(item);
                if (value is AgentChatErrorEvent error) throw AgentChatFailureProjection.CreateProviderException(error);
                yield return AgentChatWireMapper.ToUpdate((AgentChatUpdateEvent)value);
            }
        }
    }

    private sealed class ChatClient(GeneratedBehaviorRuntime runtime, Wire.RunControlChatContext context) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => runtime.Stream(context, messages, options, cancellationToken);
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

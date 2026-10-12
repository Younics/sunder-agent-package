using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Services;

// The MEAI integration owns only AI value adaptation. The supplied natural
// service operation and its generated client own dispatch and call lifetime.
internal sealed class AgentModelChatClient(
    AgentChatClientContext context,
    Func<AgentChatStreamRequest, CancellationToken, IAsyncEnumerable<AgentChatStreamEvent>> stream) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var mapped = await AgentChatMapper.FromChatMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new AgentChatStreamRequest(context.ProviderId, context.ModelId, mapped, AgentChatMapper.FromChatOptions(options));
            await foreach (var item in stream(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (item is AgentChatErrorEvent error) throw AgentChatFailureProjection.CreateProviderException(error);
                yield return await AgentChatMapper.ToUpdateAsync((AgentChatUpdateEvent)item, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await AgentChatMapper.DisposeContentAsync(mapped.SelectMany(message => message.Contents)).ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { } // The borrowed service belongs to its generated catalog/DI lifetime.
}

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Rpc.Public.Control;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Mapping = Sunder.Package.Agent.Rpc.Public.Control.RunControlMapping;

namespace Sunder.Package.Agent.Rpc.Public;

public sealed partial class RunControlService
{
    [SunderRpcMethod]
    public async IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(RunControlChatRequest request, SunderRpcInvocationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runtime = Resolve(context, request.Invocation);
        IChatClient? chat = null;
        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        Domain.AgentChatProviderException? providerException = null;
        Exception? streamException = null;
        Exception? cleanupException = null;
        try
        {
            try
            {
                chat = await runtime.CreateChatClientAsync(new Domain.AgentChatClientContext(request.Context.ProviderId.Value, request.Context.ModelId.Value,
                    CorrelationAttributes: request.Context.CorrelationAttributes.IsPresent
                        ? request.Context.CorrelationAttributes.Value.Value.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Value, StringComparer.Ordinal) : null),
                    cancellationToken).ConfigureAwait(false);
                var messages = await Protocol.AgentChatWireMapper.ToChatMessagesAsync(request.Messages.Value.Select(Mapping.ToDomain).ToArray(), context, cancellationToken).ConfigureAwait(false);
                updates = chat.GetStreamingResponseAsync(messages, Protocol.AgentChatWireMapper.ToChatOptions(Mapping.ToDomain(request.Options)), cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Domain.AgentChatProviderException exception) { providerException = exception; }
            catch (Exception exception) { streamException = exception; }

            while (providerException is null && streamException is null && updates is not null)
            {
                AgentChatStreamEvent? mapped = null;
                var hasNext = false;
                try
                {
                    hasNext = await updates.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext) mapped = Mapping.FromDomain(Protocol.AgentChatWireMapper.FromUpdate(updates.Current));
                }
                catch (Domain.AgentChatProviderException exception) { providerException = exception; }
                catch (Exception exception) { streamException = exception; }
                if (!hasNext || providerException is not null || streamException is not null) break;
                yield return mapped!;
            }
        }
        finally
        {
            if (updates is not null)
            {
                try { await updates.DisposeAsync().ConfigureAwait(false); }
                catch (Domain.AgentChatProviderException exception) { providerException ??= exception; }
                catch (Exception exception) { cleanupException ??= exception; }
            }
            if (chat is not null)
            {
                try { chat.Dispose(); }
                catch (Domain.AgentChatProviderException exception) { providerException ??= exception; }
                catch (Exception exception) { cleanupException ??= exception; }
            }
        }

        // Preserve cancellation > stream failure > provider event > cleanup failure.
        cancellationToken.ThrowIfCancellationRequested();
        if (streamException is not null) ExceptionDispatchInfo.Capture(streamException).Throw();
        if (providerException is not null)
            yield return new AgentChatErrorEvent
            {
                ErrorCode = providerException.ErrorCode ?? "provider-error", ErrorMessage = providerException.Message,
                ErrorContent = new() { Value = providerException.Content },
                ErrorFailureKind = Enum.Parse<Chat.AgentChatProviderFailureKind>(providerException.FailureKind.ToString()),
            };
        else if (cleanupException is not null) ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }
}

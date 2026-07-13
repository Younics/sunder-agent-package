using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiCodexChatClient(
    AgentChatClientContext context,
    string providerDisplayName,
    CodexConnectedTransport transport,
    CodexResponseContinuationStore continuationStore,
    OpenAiCodexSession session) : IChatClient
{
    private readonly AgentChatClientContext _context = context;
    private readonly CodexConnectedTransport _transport = transport;
    private readonly CodexResponseContinuationStore _continuationStore = continuationStore;
    private readonly OpenAiCodexSession _session = session;

    public ChatClientMetadata Metadata { get; } = new(providerDisplayName);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var modelId = options?.ModelId ?? _context.ModelId;
        return await ProviderChatResponseAggregator.AggregateAsync(
            GetStreamingResponseAsync(messages, options, cancellationToken),
            modelId,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToArray();
        var modelId = options?.ModelId ?? _context.ModelId;
        var responseId = Guid.NewGuid().ToString("N");
        var messageId = responseId;
        var toolCount = options?.ToolMode == ChatToolMode.None ? 0 : options?.Tools?.Count ?? 0;
        var telemetry = new ProviderStreamTelemetry(_context);

        await telemetry.RequestStartedAsync(
            modelId,
            messageList.Length,
            toolCount,
            options?.Instructions?.Length ?? 0,
            cancellationToken);

        await using var enumerator = _transport.StreamResponseAsync(
            _session,
            _context,
            messageList,
            options,
            _continuationStore,
            responseId,
            messageId,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate current;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                switch (ProviderStreamFailureClassifier.Classify(ex, cancellationToken))
                {
                    case ProviderStreamFailureKind.CallerCancellation:
                        await telemetry.CanceledAsync();
                        throw;
                    case ProviderStreamFailureKind.ProviderCancellation:
                        var timeout = new AgentChatProviderException(
                            "The OpenAI Codex request timed out.",
                            "### OpenAI Codex request timed out\n\nThe provider canceled the request before the caller requested cancellation.",
                            "codex-timeout",
                            ex);
                        await telemetry.FailedAsync(timeout);
                        throw timeout;
                    default:
                        await telemetry.FailedAsync(ex);
                        throw;
                }
            }

            if (current.Contents.Any(content => content is not UsageContent))
            {
                await telemetry.RecordFirstEventAsync(
                    ProviderResponseUpdates.Describe(current),
                    cancellationToken);
            }

            yield return current;
        }

        await telemetry.CompletedAsync("Provider stream ended without events.", cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? Metadata
                : null;

    public void Dispose()
    {
    }

}

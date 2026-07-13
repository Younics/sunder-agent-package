using System.Runtime.CompilerServices;
using Anthropic;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal sealed class AnthropicChatClient : IChatClient
{
    private readonly AgentChatClientContext _context;
    private readonly ProviderCredentialAccessor _credentials;
    private readonly Func<string, bool, AgentChatClientContext, IAnthropicTransport> _transportFactory;

    public AnthropicChatClient(AgentChatClientContext context, ProviderCredentialAccessor credentials)
        : this(context, credentials, CreateTransport)
    {
    }

    internal AnthropicChatClient(
        AgentChatClientContext context,
        ProviderCredentialAccessor credentials,
        Func<string, bool, AgentChatClientContext, IAnthropicTransport> transportFactory)
    {
        _context = context;
        _credentials = credentials;
        _transportFactory = transportFactory;
    }

    public ChatClientMetadata Metadata { get; } = new("Anthropic");

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
        var apiKey = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw AnthropicExceptionMapper.MissingApiKey();
        }

        var modelId = options?.ModelId ?? _context.ModelId;
        var translatedMessages = AnthropicMessageTranslator.TranslateMessages(messages);
        var request = AnthropicOptionsTranslator.Translate(translatedMessages, options, modelId);

        await new ProviderStreamTelemetry(_context).RequestStartedAsync(
            modelId,
            request.Parameters.Messages.Count,
            request.IncludeTools ? request.Parameters.Tools?.Count ?? 0 : 0,
            options?.Instructions?.Length ?? 0,
            cancellationToken);

        var transport = _transportFactory(apiKey, request.UseFastMode, _context);
        await foreach (var update in transport.SendAsync(request, modelId, cancellationToken))
        {
            yield return update;
        }
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

    private static IAnthropicTransport CreateTransport(
        string apiKey,
        bool useFastMode,
        AgentChatClientContext context)
    {
        IAnthropicClient client = new AnthropicClient { ApiKey = apiKey };
        var responseTranslator = new AnthropicResponseTranslator(context);
        return useFastMode
            ? new AnthropicFastTransport(client, responseTranslator)
            : new AnthropicStandardTransport(client, responseTranslator);
    }
}

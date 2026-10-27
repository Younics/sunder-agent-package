using System.Runtime.CompilerServices;
using Google.GenAI;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiChatClient : IChatClient
{
    private readonly AgentChatClientContext _context;
    private readonly ProviderCredentialAccessor _credentials;
    private readonly Func<string, ProviderRetryMetadata, Client> _clientFactory;
    private readonly GeminiContentTranslator _contentTranslator = new();
    private readonly GeminiOptionsTranslator _optionsTranslator = new();

    public GeminiChatClient(AgentChatClientContext context, ProviderCredentialAccessor credentials)
        : this(context, credentials, static (apiKey, metadata) => new Client(apiKey: apiKey,
            clientOptions: new Google.GenAI.Types.ClientOptions
            {
                HttpClientFactory = () => new HttpClient(new ProviderRetryMetadataHandler(metadata) { InnerHandler = new HttpClientHandler() })
                { Timeout = Timeout.InfiniteTimeSpan },
            }))
    {
    }

    internal GeminiChatClient(
        AgentChatClientContext context,
        ProviderCredentialAccessor credentials,
        Func<string, Client> clientFactory)
        : this(context, credentials, (key, _) => clientFactory(key))
    {
    }

    internal GeminiChatClient(
        AgentChatClientContext context,
        ProviderCredentialAccessor credentials,
        Func<string, ProviderRetryMetadata, Client> clientFactory)
    {
        _context = context;
        _credentials = credentials;
        _clientFactory = clientFactory;
    }

    public ChatClientMetadata Metadata { get; } = new("Google Gemini");

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
            throw GeminiExceptionMapper.MissingApiKey();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var modelId = options?.ModelId ?? _context.ModelId;
        var includeTools = GeminiOptionsTranslator.ShouldIncludeTools(options);
        var translation = _contentTranslator.Translate(messages);
        var config = _optionsTranslator.Translate(options, includeTools, translation.SystemInstruction, modelId);

        var telemetry = new ProviderStreamTelemetry(_context);
        await telemetry.RequestStartedAsync(
            modelId,
            translation.Contents.Count,
            includeTools ? options?.Tools?.Count ?? 0 : 0,
            options?.Instructions?.Length ?? 0,
            cancellationToken);

        Client client;
        var retryMetadata = new ProviderRetryMetadata();
        try
        {
            client = _clientFactory(apiKey, retryMetadata);
        }
        catch (Exception ex)
        {
            throw GeminiExceptionMapper.ClientInitialization(ex);
        }

        using (client)
        {
            var responseTranslator = new GeminiResponseTranslator();
            var transport = includeTools
                ? new GeminiCompletionTransport(responseTranslator, telemetry, retryMetadata).StreamAsync(
                    client,
                    translation.Contents,
                    config,
                    modelId,
                    options?.AllowMultipleToolCalls == true,
                    cancellationToken)
                : new GeminiStreamingTransport(responseTranslator, telemetry, retryMetadata).StreamAsync(
                    client,
                    translation.Contents,
                    config,
                    modelId,
                    cancellationToken);

            await foreach (var update in transport.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
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
}

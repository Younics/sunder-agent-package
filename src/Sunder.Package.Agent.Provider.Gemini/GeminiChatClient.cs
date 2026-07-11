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
    private readonly Func<string, Client> _clientFactory;
    private readonly GeminiContentTranslator _contentTranslator = new();
    private readonly GeminiOptionsTranslator _optionsTranslator = new();
    private readonly GeminiCompletionTransport _completionTransport;
    private readonly GeminiStreamingTransport _streamingTransport;
    private readonly GeminiTelemetry _telemetry;

    public GeminiChatClient(AgentChatClientContext context, ProviderCredentialAccessor credentials)
        : this(context, credentials, static apiKey => new Client(apiKey: apiKey))
    {
    }

    internal GeminiChatClient(
        AgentChatClientContext context,
        ProviderCredentialAccessor credentials,
        Func<string, Client> clientFactory)
    {
        _context = context;
        _credentials = credentials;
        _clientFactory = clientFactory;
        _telemetry = new GeminiTelemetry(context);
        var responseTranslator = new GeminiResponseTranslator();
        _completionTransport = new GeminiCompletionTransport(responseTranslator, _telemetry);
        _streamingTransport = new GeminiStreamingTransport(responseTranslator, _telemetry);
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

        await _telemetry.RequestStartedAsync(
            modelId,
            translation.Contents.Count,
            includeTools ? options?.Tools?.Count ?? 0 : 0,
            options?.Instructions?.Length ?? 0,
            cancellationToken);

        Client client;
        try
        {
            client = _clientFactory(apiKey);
        }
        catch (Exception ex)
        {
            throw GeminiExceptionMapper.ClientInitialization(ex);
        }

        using (client)
        {
            var transport = includeTools
                ? _completionTransport.StreamAsync(
                    client,
                    translation.Contents,
                    config,
                    modelId,
                    options?.AllowMultipleToolCalls == true,
                    cancellationToken)
                : _streamingTransport.StreamAsync(
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

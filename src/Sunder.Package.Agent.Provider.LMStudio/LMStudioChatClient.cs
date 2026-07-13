using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed class LMStudioChatClient(
    AgentChatClientContext context,
    LMStudioConnection connection) : IChatClient
{
    private readonly AgentChatClientContext _context = context;
    private readonly LMStudioConnection _connection = connection;

    public ChatClientMetadata Metadata { get; } = new("LM Studio");

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
        var connection = await _connection.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
        if (connection.Options is not { } connectionOptions)
        {
            throw LMStudioExceptionMapper.InvalidConfiguration(connection.ValidationError ?? "Invalid LM Studio configuration.");
        }

        var modelId = options?.ModelId ?? _context.ModelId;
        var sdkMessages = LMStudioOpenAIMessageTranslator.Translate(messages, options?.Instructions);
        var sdkOptions = LMStudioOpenAIOptionsTranslator.Translate(options);
        var responseId = Guid.NewGuid().ToString("N");
        var telemetry = new ProviderStreamTelemetry(_context);

        await telemetry.RequestStartedAsync(
            modelId,
            sdkMessages.Count,
            sdkOptions.Tools.Count,
            options?.Instructions?.Length ?? 0,
            cancellationToken);

        IAsyncEnumerable<StreamingChatCompletionUpdate> stream;
        try
        {
            var client = _connection.CreateChatClient(modelId, connectionOptions);
            stream = client.CompleteChatStreamingAsync(sdkMessages, sdkOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            throw ProviderStreamFailureClassifier.Classify(
                ex,
                cancellationToken,
                inspectInnerExceptions: true,
                includeTimeouts: true) switch
            {
                ProviderStreamFailureKind.CallerCancellation => new OperationCanceledException(
                    "The LM Studio request was canceled by the caller.", ex, cancellationToken),
                ProviderStreamFailureKind.ProviderCancellation => LMStudioExceptionMapper.ProviderTimeout(ex),
                _ => LMStudioExceptionMapper.Map(ex),
            };
        }

        var translator = new LMStudioOpenAIStreamTranslator(options?.AllowMultipleToolCalls == true);

        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            StreamingChatCompletionUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                update = enumerator.Current;
            }
            catch (Exception ex)
            {
                switch (ProviderStreamFailureClassifier.Classify(
                            ex,
                            cancellationToken,
                            inspectInnerExceptions: true,
                            includeTimeouts: true))
                {
                    case ProviderStreamFailureKind.CallerCancellation:
                        await telemetry.CanceledAsync();
                        throw new OperationCanceledException(
                            "The LM Studio request was canceled by the caller.",
                            ex,
                            cancellationToken);
                    case ProviderStreamFailureKind.ProviderCancellation:
                        var timeout = LMStudioExceptionMapper.ProviderTimeout(ex);
                        await telemetry.FailedAsync(timeout);
                        throw timeout;
                    default:
                        await telemetry.FailedAsync(ex);
                        throw LMStudioExceptionMapper.Map(ex);
                }
            }

            var translated = translator.Translate(update, responseId, responseId, modelId);
            foreach (var responseUpdate in translated.Updates)
            {
                if (responseUpdate.Contents.Any(content => content is not UsageContent))
                {
                    await telemetry.RecordFirstEventAsync(
                        ProviderResponseUpdates.Describe(responseUpdate),
                        cancellationToken);
                }
                yield return responseUpdate;
            }

            if (translated.IsTerminal)
            {
                await telemetry.CompletedAsync("Provider completed without content.", cancellationToken);
                yield break;
            }
        }

        throw LMStudioExceptionMapper.IncompleteResponse(
            "The LM Studio stream ended before a successful finish reason.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? Metadata
                : null;

    public void Dispose()
    {
        // The package-scoped connection owns the shared transport.
    }

}

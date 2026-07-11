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

        await LogStartAsync(modelId, sdkMessages.Count, sdkOptions.Tools.Count, options?.Instructions?.Length ?? 0, cancellationToken);

        IAsyncEnumerable<StreamingChatCompletionUpdate> stream;
        try
        {
            var client = _connection.CreateChatClient(modelId, connectionOptions);
            stream = client.CompleteChatStreamingAsync(sdkMessages, sdkOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw LMStudioExceptionMapper.Map(ex);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var firstEventRecorded = false;
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
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                await LogAsync(
                    AgentLogLevel.Error,
                    "provider.stream.failed",
                    ex.Message,
                    stopwatch.ElapsedMilliseconds,
                    exception: ex,
                    cancellationToken: CancellationToken.None);
                throw LMStudioExceptionMapper.ProviderTimeout(ex);
            }
            catch (OperationCanceledException)
            {
                await LogAsync(
                    AgentLogLevel.Warning,
                    "provider.stream.canceled",
                    "Provider stream was canceled.",
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                if (LMStudioExceptionMapper.ContainsCancellation(ex))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await LogAsync(
                            AgentLogLevel.Warning,
                            "provider.stream.canceled",
                            "Provider stream was canceled.",
                            stopwatch.ElapsedMilliseconds,
                            cancellationToken: CancellationToken.None);
                        throw new OperationCanceledException(
                            "The LM Studio request was canceled by the caller.",
                            ex,
                            cancellationToken);
                    }

                    var timeout = LMStudioExceptionMapper.ProviderTimeout(ex);
                    await LogAsync(
                        AgentLogLevel.Error,
                        "provider.stream.failed",
                        timeout.Message,
                        stopwatch.ElapsedMilliseconds,
                        exception: timeout,
                        cancellationToken: CancellationToken.None);
                    throw timeout;
                }

                await LogAsync(
                    AgentLogLevel.Error,
                    "provider.stream.failed",
                    ex.Message,
                    stopwatch.ElapsedMilliseconds,
                    exception: ex,
                    cancellationToken: CancellationToken.None);
                throw LMStudioExceptionMapper.Map(ex);
            }

            var translated = translator.Translate(update, responseId, responseId, modelId);
            foreach (var responseUpdate in translated.Updates)
            {
                firstEventRecorded = await RecordFirstEventAsync(
                    firstEventRecorded,
                    responseUpdate.Contents.OfType<FunctionCallContent>().Any() ? "ToolCallRequested" : "TextDelta",
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
                yield return responseUpdate;
            }

            if (translated.IsTerminal)
            {
                await LogAsync(
                    AgentLogLevel.Debug,
                    "provider.stream.completed",
                    firstEventRecorded ? null : "Provider completed without content.",
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken: cancellationToken);
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

    private async ValueTask LogStartAsync(
        string modelId,
        int messageCount,
        int toolCount,
        int systemPromptLength,
        CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model.id"] = modelId,
            ["prompt.turn_count"] = messageCount,
            ["tool.available_count"] = toolCount,
            ["system_prompt.length"] = systemPromptLength,
        };
        await LogAsync(
            AgentLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: attributes,
            cancellationToken: cancellationToken);
        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.start",
            "Provider stream started.",
            attributes: new Dictionary<string, object?>(attributes, StringComparer.Ordinal)
            {
                ["provider.id"] = _context.ProviderId,
                ["tool.count"] = toolCount,
                ["message.count"] = messageCount,
            },
            cancellationToken: cancellationToken);
    }

    private async ValueTask<bool> RecordFirstEventAsync(
        bool firstEventRecorded,
        string eventType,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (firstEventRecorded)
        {
            return true;
        }

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.first_event",
            eventType,
            elapsedMilliseconds,
            cancellationToken: cancellationToken);
        return true;
    }

    private ValueTask LogAsync(
        AgentLogLevel level,
        string eventName,
        string? message = null,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => _context.LogProviderEventAsync(
            level,
            eventName,
            message ?? eventName,
            elapsedMilliseconds,
            attributes,
            exception,
            cancellationToken);
}

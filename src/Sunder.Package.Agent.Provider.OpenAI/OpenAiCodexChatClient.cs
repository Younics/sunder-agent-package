using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed class OpenAiCodexChatClient(
    AgentChatClientContext context,
    string providerDisplayName,
    CodexConnectedTransport transport,
    OpenAiCodexSession session) : IChatClient
{
    private readonly AgentChatClientContext _context = context;
    private readonly CodexConnectedTransport _transport = transport;
    private readonly OpenAiCodexSession _session = session;

    public ChatClientMetadata Metadata { get; } = new(providerDisplayName);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var responseMessage = new AIChatMessage(AIChatRole.Assistant, []);
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                responseMessage.Contents.Add(content);
            }
        }

        return new ChatResponse(responseMessage)
        {
            ModelId = options?.ModelId ?? _context.ModelId,
        };
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

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model.id"] = modelId,
                ["prompt.turn_count"] = messageList.Length,
                ["tool.available_count"] = toolCount,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);

        var streamStopwatch = Stopwatch.StartNew();
        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.start",
            "Provider stream started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["provider.id"] = _context.ProviderId,
                ["model.id"] = modelId,
                ["tool.count"] = toolCount,
                ["message.count"] = messageList.Length,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);

        var firstEventRecorded = false;
        await using var enumerator = _transport.StreamResponseAsync(
            _session,
            _context,
            messageList,
            options,
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
            catch (OperationCanceledException)
            {
                await LogAsync(AgentLogLevel.Warning, "provider.stream.canceled", "Provider stream was canceled.", streamStopwatch.ElapsedMilliseconds, cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, streamStopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
                throw;
            }

            if (!firstEventRecorded)
            {
                firstEventRecorded = true;
                await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", DescribeUpdate(current), streamStopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            }

            yield return current;
        }

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.completed",
            firstEventRecorded ? null : "Provider stream ended without events.",
            streamStopwatch.ElapsedMilliseconds,
            cancellationToken: cancellationToken);
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

    private ValueTask LogAsync(
        AgentLogLevel level,
        string eventName,
        string? message = null,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => _context.LogProviderEventAsync(level, eventName, message ?? eventName, elapsedMilliseconds, attributes, exception, cancellationToken);

    private static string DescribeUpdate(ChatResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            return "TextDelta";
        }

        return update.Contents.Any(content => content is FunctionCallContent)
            ? "ToolCallRequested"
            : "Update";
    }
}

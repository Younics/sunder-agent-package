using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using BetaContentBlock = Anthropic.Models.Beta.Messages.BetaContentBlock;
using BetaRawMessageStreamEvent = Anthropic.Models.Beta.Messages.BetaRawMessageStreamEvent;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal sealed class AnthropicResponseTranslator(AgentChatClientContext context)
{
    internal async IAsyncEnumerable<ChatResponseUpdate> TranslateToolResponseAsync<TResponse, TContent>(
        Func<CancellationToken, Task<TResponse>> createResponseAsync,
        Func<TResponse, IReadOnlyList<TContent>> getContent,
        Func<TResponse, string?> getStopReason,
        Func<TContent, TextReasoningContent?> getReasoningContent,
        Func<TContent, AnthropicToolCall?> getToolCall,
        Func<TContent, string?> getText,
        string modelId,
        bool allowMultipleToolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        TResponse response;
        try
        {
            response = await createResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
            throw AnthropicExceptionMapper.ProviderTimeout(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
            throw AnthropicExceptionMapper.Map(ex);
        }

        var responseId = Guid.NewGuid().ToString("N");
        var content = getContent(response);
        var toolCalls = content.Select(getToolCall).OfType<AnthropicToolCall>().ToArray();
        if (toolCalls.Length > 1 && !allowMultipleToolCalls)
        {
            throw AnthropicExceptionMapper.MultipleToolCalls();
        }

        ValidateTerminalReason(getStopReason(response), toolCalls.Length > 0);
        var translatedContent = new List<AIContent>(content.Count);
        foreach (var block in content)
        {
            if (getReasoningContent(block) is { } reasoningContent)
            {
                translatedContent.Add(reasoningContent);
                continue;
            }

            if (getToolCall(block) is { } toolCall)
            {
                translatedContent.Add(TranslateToolCall(toolCall));
                continue;
            }

            if (getText(block) is { Length: > 0 } text)
            {
                translatedContent.Add(new TextContent(text));
            }
        }

        if (translatedContent.Count > 0)
        {
            var firstEventKind = translatedContent[0] switch
            {
                TextReasoningContent => "ReasoningDelta",
                FunctionCallContent => "ToolCallRequested",
                _ => "TextDelta",
            };
            await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", firstEventKind, stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            yield return CreateUpdate(
                modelId,
                responseId,
                translatedContent.ToArray(),
                toolCalls.Length > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);
        }

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.completed",
            translatedContent.Count == 0 ? "Provider completed without content." : null,
            stopwatch.ElapsedMilliseconds,
            cancellationToken: cancellationToken);
    }

    internal async IAsyncEnumerable<ChatResponseUpdate> TranslateStreamingResponseAsync<TStreamEvent>(
        Func<IAsyncEnumerable<TStreamEvent>> createStream,
        Func<TStreamEvent, AnthropicStreamingContent> getStreamingContent,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<TStreamEvent> stream;
        try
        {
            stream = createStream();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw AnthropicExceptionMapper.Map(ex);
        }

        var responseId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var firstEventRecorded = false;
        string? stopReason = null;
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            TStreamEvent rawEvent;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                rawEvent = enumerator.Current;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
                throw AnthropicExceptionMapper.ProviderTimeout(ex);
            }
            catch (OperationCanceledException)
            {
                await LogAsync(AgentLogLevel.Warning, "provider.stream.canceled", "Provider stream was canceled.", stopwatch.ElapsedMilliseconds, cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
                throw AnthropicExceptionMapper.Map(ex);
            }

            var content = getStreamingContent(rawEvent);
            if (!string.IsNullOrWhiteSpace(content.StopReason))
            {
                stopReason = content.StopReason;
            }

            if (content.IsTerminal)
            {
                ValidateTerminalReason(stopReason, hasToolCalls: false);
                await LogAsync(
                    AgentLogLevel.Debug,
                    "provider.stream.completed",
                    firstEventRecorded ? null : "Provider completed without content.",
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken: cancellationToken);
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(content.ReasoningText))
            {
                if (!firstEventRecorded)
                {
                    firstEventRecorded = true;
                    await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "ReasoningDelta", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
                }

                yield return CreateUpdate(modelId, responseId, new TextReasoningContent(content.ReasoningText));
                continue;
            }

            if (string.IsNullOrWhiteSpace(content.Text))
            {
                continue;
            }

            if (!firstEventRecorded)
            {
                firstEventRecorded = true;
                await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "TextDelta", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            }

            yield return CreateUpdate(modelId, responseId, content.Text);
        }

        throw AnthropicExceptionMapper.IncompleteResponse(
            "The Anthropic event stream ended before a message_stop event.");
    }

    internal static TextReasoningContent? GetReasoningContent(ContentBlock block)
        => block.TryPickThinking(out var thinking) && thinking is not null
            ? new TextReasoningContent(thinking.Thinking) { ProtectedData = thinking.Signature }
            : null;

    internal static TextReasoningContent? GetReasoningContent(BetaContentBlock block)
        => block.TryPickThinking(out var thinking) && thinking is not null
            ? new TextReasoningContent(thinking.Thinking) { ProtectedData = thinking.Signature }
            : null;

    internal static AnthropicToolCall? GetToolCall(ContentBlock block)
        => block.TryPickToolUse(out var toolUse)
            ? new AnthropicToolCall(toolUse!.ID, toolUse.Name, GetToolInput(() => toolUse.Input, toolUse.RawData))
            : null;

    internal static AnthropicToolCall? GetToolCall(BetaContentBlock block)
        => block.TryPickToolUse(out var toolUse)
            ? new AnthropicToolCall(toolUse!.ID, toolUse.Name, GetToolInput(() => toolUse.Input, toolUse.RawData))
            : null;

    internal static string? GetText(ContentBlock block)
        => block.TryPickText(out var text) ? text?.Text : null;

    internal static string? GetText(BetaContentBlock block)
        => block.TryPickText(out var text) ? text?.Text : null;

    internal static AnthropicStreamingContent GetStreamingContent(RawMessageStreamEvent rawEvent)
    {
        if (rawEvent.TryPickDelta(out var messageDelta))
        {
            return new AnthropicStreamingContent(null, null, GetStopReason(messageDelta.RawData), false);
        }

        if (rawEvent.TryPickStop(out _))
        {
            return new AnthropicStreamingContent(null, null, null, true);
        }

        if (!rawEvent.TryPickContentBlockDelta(out var delta))
        {
            return default;
        }

        if (delta.Delta.TryPickThinking(out var thinking) && !string.IsNullOrWhiteSpace(thinking.Thinking))
        {
            return new AnthropicStreamingContent(thinking.Thinking, null, null, false);
        }

        return delta.Delta.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text)
            ? new AnthropicStreamingContent(null, text.Text, null, false)
            : default;
    }

    internal static AnthropicStreamingContent GetStreamingContent(BetaRawMessageStreamEvent rawEvent)
    {
        if (rawEvent.TryPickDelta(out var messageDelta))
        {
            return new AnthropicStreamingContent(null, null, GetStopReason(messageDelta.RawData), false);
        }

        if (rawEvent.TryPickStop(out _))
        {
            return new AnthropicStreamingContent(null, null, null, true);
        }

        if (!rawEvent.TryPickContentBlockDelta(out var delta))
        {
            return default;
        }

        if (delta.Delta.TryPickThinking(out var thinking) && !string.IsNullOrWhiteSpace(thinking.Thinking))
        {
            return new AnthropicStreamingContent(thinking.Thinking, null, null, false);
        }

        return delta.Delta.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text)
            ? new AnthropicStreamingContent(null, text.Text, null, false)
            : default;
    }

    internal static FunctionCallContent TranslateToolCall(AnthropicToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(toolCall.Id) || string.IsNullOrWhiteSpace(toolCall.Name))
        {
            throw AnthropicExceptionMapper.MalformedToolCall(
                "Anthropic returned a tool call without an ID or function name.");
        }

        if (!ProviderJson.TryParseObjectArguments(JsonSerializer.Serialize(toolCall.Input), out var arguments))
        {
            throw AnthropicExceptionMapper.MalformedToolCall(
                $"Anthropic returned non-object or malformed arguments for tool '{toolCall.Name}'.");
        }

        return new FunctionCallContent(toolCall.Id, toolCall.Name, arguments);
    }

    private static object? GetToolInput(
        Func<IReadOnlyDictionary<string, JsonElement>> getInput,
        IReadOnlyDictionary<string, JsonElement> rawData)
    {
        try
        {
            return getInput();
        }
        catch (AnthropicInvalidDataException)
        {
            return rawData.TryGetValue("input", out var input) ? input.Clone() : null;
        }
    }

    private static ChatResponseUpdate CreateUpdate(string modelId, string responseId, AIContent content)
        => new(AIChatRole.Assistant, [content])
        {
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = modelId,
        };

    private static ChatResponseUpdate CreateUpdate(
        string modelId,
        string responseId,
        AIContent[] content,
        ChatFinishReason finishReason)
        => new(AIChatRole.Assistant, content)
        {
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = modelId,
            FinishReason = finishReason,
        };

    private static ChatResponseUpdate CreateUpdate(string modelId, string responseId, string text)
        => new(AIChatRole.Assistant, text)
        {
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = modelId,
        };

    private ValueTask LogAsync(
        AgentLogLevel level,
        string eventName,
        string? message = null,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => context.LogProviderEventAsync(level, eventName, message ?? eventName, elapsedMilliseconds, attributes, exception, cancellationToken);

    private static void ValidateTerminalReason(string? stopReason, bool hasToolCalls)
    {
        var normalizedReason = stopReason?
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (string.Equals(normalizedReason, "tooluse", StringComparison.OrdinalIgnoreCase) && hasToolCalls)
        {
            return;
        }

        if (!hasToolCalls && (string.Equals(normalizedReason, "endturn", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(normalizedReason, "stopsequence", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw AnthropicExceptionMapper.IncompleteResponse(string.IsNullOrWhiteSpace(stopReason)
            ? "Anthropic did not provide a terminal stop reason."
            : $"Anthropic stopped with '{stopReason}'.");
    }

    private static string? GetStopReason(IReadOnlyDictionary<string, JsonElement> rawData)
    {
        if (!rawData.TryGetValue("delta", out var delta)
            || delta.ValueKind != JsonValueKind.Object
            || !delta.TryGetProperty("stop_reason", out var stopReason)
            || stopReason.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return stopReason.GetString();
    }
}

internal sealed record AnthropicToolCall(string? Id, string? Name, object? Input);

internal readonly record struct AnthropicStreamingContent(
    string? ReasoningText,
    string? Text,
    string? StopReason,
    bool IsTerminal);

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
    private readonly ProviderStreamTelemetry _telemetry = new(context);

    internal async IAsyncEnumerable<ChatResponseUpdate> TranslateToolResponseAsync<TResponse, TContent>(
        Func<CancellationToken, Task<TResponse>> createResponseAsync,
        Func<TResponse, IReadOnlyList<TContent>> getContent,
        Func<TResponse, string?> getStopReason,
        Func<TResponse, ProviderUsageSnapshot> getUsage,
        Func<TContent, TextReasoningContent?> getReasoningContent,
        Func<TContent, AnthropicToolCall?> getToolCall,
        Func<TContent, string?> getText,
        string modelId,
        bool allowMultipleToolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TResponse response;
        try
        {
            response = await createResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            switch (ProviderStreamFailureClassifier.Classify(ex, cancellationToken))
            {
                case ProviderStreamFailureKind.CallerCancellation:
                    await _telemetry.CanceledAsync();
                    throw;
                case ProviderStreamFailureKind.ProviderCancellation:
                    await _telemetry.FailedAsync(ex);
                    throw AnthropicExceptionMapper.ProviderTimeout((OperationCanceledException)ex);
                default:
                    await _telemetry.FailedAsync(ex);
                    throw AnthropicExceptionMapper.Map(ex);
            }
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
            var update = ProviderResponseUpdates.Create(
                modelId,
                responseId,
                responseId,
                translatedContent,
                toolCalls.Length > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);
            await _telemetry.RecordFirstEventAsync(ProviderResponseUpdates.Describe(update), cancellationToken);
            yield return update;
        }

        if (ProviderResponseUpdates.CreateUsage(modelId, responseId, responseId, getUsage(response)) is { } usageUpdate)
        {
            yield return usageUpdate;
        }

        await _telemetry.CompletedAsync("Provider completed without content.", cancellationToken);
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
        var usage = new ProviderUsageAccumulator();
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
            catch (Exception ex)
            {
                switch (ProviderStreamFailureClassifier.Classify(ex, cancellationToken))
                {
                    case ProviderStreamFailureKind.CallerCancellation:
                        await _telemetry.CanceledAsync();
                        throw;
                    case ProviderStreamFailureKind.ProviderCancellation:
                        await _telemetry.FailedAsync(ex);
                        throw AnthropicExceptionMapper.ProviderTimeout((OperationCanceledException)ex);
                    default:
                        await _telemetry.FailedAsync(ex);
                        throw AnthropicExceptionMapper.Map(ex);
                }
            }

            var content = getStreamingContent(rawEvent);
            usage.SetLatest(content.Usage);
            if (!string.IsNullOrWhiteSpace(content.StopReason))
            {
                stopReason = content.StopReason;
            }

            if (content.IsTerminal)
            {
                ValidateTerminalReason(stopReason, hasToolCalls: false);
                if (usage.CreateContent() is { } usageContent)
                {
                    yield return ProviderResponseUpdates.Create(modelId, responseId, responseId, usageContent);
                }

                await _telemetry.CompletedAsync("Provider completed without content.", cancellationToken);
                yield break;
            }

            if (content.UnsupportedEventKind is { } unsupportedEventKind)
            {
                await _telemetry.UnsupportedResponseAsync("Anthropic", [unsupportedEventKind], cancellationToken);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(content.ReasoningText))
            {
                await _telemetry.RecordFirstEventAsync("ReasoningDelta", cancellationToken);
                yield return ProviderResponseUpdates.Create(
                    modelId,
                    responseId,
                    responseId,
                    new TextReasoningContent(content.ReasoningText));
                continue;
            }

            if (string.IsNullOrWhiteSpace(content.Text))
            {
                continue;
            }

            await _telemetry.RecordFirstEventAsync("TextDelta", cancellationToken);
            yield return ProviderResponseUpdates.CreateText(modelId, responseId, responseId, content.Text);
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
        if (rawEvent.TryPickStart(out var messageStart))
        {
            return new AnthropicStreamingContent(Usage: GetUsage(messageStart.RawData, usageUnderMessage: true));
        }

        if (rawEvent.TryPickDelta(out var messageDelta))
        {
            return new AnthropicStreamingContent(
                StopReason: GetStopReason(messageDelta.RawData),
                Usage: GetUsage(messageDelta.RawData, usageUnderMessage: false));
        }

        if (rawEvent.TryPickStop(out _))
        {
            return new AnthropicStreamingContent(IsTerminal: true);
        }

        if (!rawEvent.TryPickContentBlockDelta(out var delta))
        {
            return default;
        }

        if (delta.Delta.TryPickThinking(out var thinking) && !string.IsNullOrWhiteSpace(thinking.Thinking))
        {
            return new AnthropicStreamingContent(ReasoningText: thinking.Thinking);
        }

        return delta.Delta.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text)
            ? new AnthropicStreamingContent(Text: text.Text)
            : new AnthropicStreamingContent(UnsupportedEventKind: delta.Delta.GetType().Name);
    }

    internal static AnthropicStreamingContent GetStreamingContent(BetaRawMessageStreamEvent rawEvent)
    {
        if (rawEvent.TryPickStart(out var messageStart))
        {
            return new AnthropicStreamingContent(Usage: GetUsage(messageStart.RawData, usageUnderMessage: true));
        }

        if (rawEvent.TryPickDelta(out var messageDelta))
        {
            return new AnthropicStreamingContent(
                StopReason: GetStopReason(messageDelta.RawData),
                Usage: GetUsage(messageDelta.RawData, usageUnderMessage: false));
        }

        if (rawEvent.TryPickStop(out _))
        {
            return new AnthropicStreamingContent(IsTerminal: true);
        }

        if (!rawEvent.TryPickContentBlockDelta(out var delta))
        {
            return default;
        }

        if (delta.Delta.TryPickThinking(out var thinking) && !string.IsNullOrWhiteSpace(thinking.Thinking))
        {
            return new AnthropicStreamingContent(ReasoningText: thinking.Thinking);
        }

        return delta.Delta.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text)
            ? new AnthropicStreamingContent(Text: text.Text)
            : new AnthropicStreamingContent(UnsupportedEventKind: delta.Delta.GetType().Name);
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

    internal static ProviderUsageSnapshot GetUsage(global::Anthropic.Models.Messages.Usage usage)
        => new(
            SumInputTokens(usage.InputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens),
            usage.OutputTokens,
            CachedInputTokenCount: usage.CacheReadInputTokens);

    internal static ProviderUsageSnapshot GetUsage(global::Anthropic.Models.Beta.Messages.BetaUsage usage)
        => new(
            SumInputTokens(usage.InputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens),
            usage.OutputTokens,
            CachedInputTokenCount: usage.CacheReadInputTokens);

    private static long? SumInputTokens(long? inputTokens, long? cacheCreationInputTokens, long? cacheReadInputTokens)
        => inputTokens is not null || cacheCreationInputTokens is not null || cacheReadInputTokens is not null
            ? (inputTokens ?? 0) + (cacheCreationInputTokens ?? 0) + (cacheReadInputTokens ?? 0)
            : null;

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

    private static ProviderUsageSnapshot GetUsage(
        IReadOnlyDictionary<string, JsonElement> rawData,
        bool usageUnderMessage)
    {
        if (usageUnderMessage
            && rawData.TryGetValue("message", out var message)
            && message.ValueKind == JsonValueKind.Object)
        {
            return GetUsage(message);
        }

        return GetUsage(rawData);
    }

    private static ProviderUsageSnapshot GetUsage(JsonElement container)
        => container.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? ParseUsage(usage)
            : default;

    private static ProviderUsageSnapshot GetUsage(IReadOnlyDictionary<string, JsonElement> container)
        => container.TryGetValue("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? ParseUsage(usage)
            : default;

    private static ProviderUsageSnapshot ParseUsage(JsonElement usage)
    {
        var baseInput = GetInt64(usage, "input_tokens");
        var cacheCreation = GetInt64(usage, "cache_creation_input_tokens");
        var cacheRead = GetInt64(usage, "cache_read_input_tokens");
        long? input = baseInput is not null || cacheCreation is not null || cacheRead is not null
            ? (baseInput ?? 0) + (cacheCreation ?? 0) + (cacheRead ?? 0)
            : null;
        return new ProviderUsageSnapshot(
            input,
            GetInt64(usage, "output_tokens"),
            CachedInputTokenCount: cacheRead);
    }

    private static long? GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : null;
}

internal sealed record AnthropicToolCall(string? Id, string? Name, object? Input);

internal readonly record struct AnthropicStreamingContent(
    string? ReasoningText = null,
    string? Text = null,
    string? StopReason = null,
    bool IsTerminal = false,
    ProviderUsageSnapshot Usage = default,
    string? UnsupportedEventKind = null);

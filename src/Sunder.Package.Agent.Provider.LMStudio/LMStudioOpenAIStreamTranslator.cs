using System.Text;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed record LMStudioStreamTranslation(
    IReadOnlyList<ChatResponseUpdate> Updates,
    bool IsTerminal);

internal sealed class LMStudioOpenAIStreamTranslator(bool allowMultipleToolCalls)
{
    private readonly bool _allowMultipleToolCalls = allowMultipleToolCalls;
    private readonly List<ChatResponseUpdate> _bufferedTextUpdates = [];
    private readonly SortedDictionary<int, ToolCallAccumulator> _toolCalls = [];

    public LMStudioStreamTranslation Translate(
        StreamingChatCompletionUpdate update,
        string responseId,
        string messageId,
        string modelId)
    {
        var translated = new List<ChatResponseUpdate>();
        foreach (var contentPart in update.ContentUpdate)
        {
            if (!string.IsNullOrEmpty(contentPart.Text))
            {
                var textUpdate = ProviderResponseUpdates.CreateText(
                    modelId,
                    responseId,
                    messageId,
                    contentPart.Text);
                if (_toolCalls.Count == 0)
                {
                    translated.Add(textUpdate);
                }
                else
                {
                    _bufferedTextUpdates.Add(textUpdate);
                }
            }
        }

        foreach (var toolCallUpdate in update.ToolCallUpdates)
        {
            ApplyToolCallDelta(
                toolCallUpdate.Index,
                toolCallUpdate.ToolCallId,
                toolCallUpdate.FunctionName,
                toolCallUpdate.FunctionArgumentsUpdate?.ToString());
        }

        if (update.Usage is { } usage
            && ProviderResponseUpdates.CreateUsage(
                modelId,
                responseId,
                messageId,
                new ProviderUsageSnapshot(
                    usage.InputTokenCount,
                    usage.OutputTokenCount,
                    usage.TotalTokenCount,
                    usage.InputTokenDetails?.CachedTokenCount,
                    usage.OutputTokenDetails?.ReasoningTokenCount)) is { } usageUpdate)
        {
            translated.Add(usageUpdate);
        }

        var isTerminal = update.FinishReason is not null;
        if (isTerminal)
        {
            if (update.FinishReason == OpenAI.Chat.ChatFinishReason.Length)
            {
                // Do not parse or emit a function call cut off in mid-arguments.
                translated.AddRange(_bufferedTextUpdates);
                _bufferedTextUpdates.Clear();
                _toolCalls.Clear();
                translated.Add(ProviderResponseUpdates.Create(modelId, responseId, messageId,
                    Array.Empty<AIContent>(), Microsoft.Extensions.AI.ChatFinishReason.Length));
                return new LMStudioStreamTranslation(translated, IsTerminal: true);
            }

            if (update.FinishReason == OpenAI.Chat.ChatFinishReason.ContentFilter)
            {
                throw LMStudioExceptionMapper.IncompleteResponse(
                    "LM Studio stopped because output was filtered for safety.");
            }

            if (update.FinishReason != OpenAI.Chat.ChatFinishReason.Stop
                && update.FinishReason != OpenAI.Chat.ChatFinishReason.ToolCalls
                && update.FinishReason != OpenAI.Chat.ChatFinishReason.FunctionCall)
            {
                throw LMStudioExceptionMapper.IncompleteResponse(
                    $"LM Studio stopped with unsupported finish reason '{update.FinishReason}'.");
            }

            var toolCall = Complete(responseId, messageId, modelId);
            if (update.FinishReason != OpenAI.Chat.ChatFinishReason.Stop && toolCall is null)
            {
                throw LMStudioExceptionMapper.MalformedToolCall(
                    "LM Studio ended with a tool-call finish reason but did not provide a complete tool call.");
            }

            if (toolCall is not null)
            {
                translated.Add(toolCall);
                translated.AddRange(_bufferedTextUpdates);
                _bufferedTextUpdates.Clear();
            }
            else
                translated.Add(ProviderResponseUpdates.Create(modelId, responseId, messageId,
                    Array.Empty<AIContent>(), Microsoft.Extensions.AI.ChatFinishReason.Stop));
        }

        return new LMStudioStreamTranslation(translated, isTerminal);
    }

    internal void ApplyToolCallDelta(
        int index,
        string? callId,
        string? functionName,
        string? argumentsDelta)
    {
        if (!_toolCalls.TryGetValue(index, out var accumulator))
        {
            if (!_allowMultipleToolCalls && _toolCalls.Count > 0)
            {
                throw LMStudioExceptionMapper.MultipleToolCalls();
            }

            accumulator = new ToolCallAccumulator();
            _toolCalls[index] = accumulator;
        }

        accumulator.Apply(callId, functionName, argumentsDelta);
    }

    public ChatResponseUpdate? Complete(string responseId, string messageId, string modelId)
    {
        var completed = new List<AgentToolCallRequest>();
        foreach (var accumulator in _toolCalls.Values)
        {
            if (!accumulator.TryBuild(out var toolCall))
            {
                throw LMStudioExceptionMapper.MalformedToolCall(
                    "LM Studio streamed a tool call without a function name.");
            }

            completed.Add(toolCall);
        }

        if (completed.Count == 0)
        {
            return null;
        }

        var functionCalls = new List<FunctionCallContent>(completed.Count);
        foreach (var toolCall in completed)
        {
            if (!ProviderJson.TryParseObjectArguments(toolCall.ArgumentsJson, out var arguments))
            {
                throw LMStudioExceptionMapper.MalformedToolCall(
                    $"LM Studio returned non-object or malformed arguments for tool '{toolCall.ToolId}'.");
            }

            functionCalls.Add(new FunctionCallContent(toolCall.CallId, toolCall.ToolId, arguments));
        }

        return ProviderResponseUpdates.Create(
            modelId,
            responseId,
            messageId,
            functionCalls,
            Microsoft.Extensions.AI.ChatFinishReason.ToolCalls);
    }

    private sealed class ToolCallAccumulator
    {
        private readonly StringBuilder _arguments = new();
        private string? _callId;
        private string? _toolId;

        public void Apply(string? callId, string? toolId, string? argumentsDelta)
        {
            if (!string.IsNullOrWhiteSpace(callId))
            {
                if (_callId is not null && !string.Equals(_callId, callId, StringComparison.Ordinal))
                {
                    throw LMStudioExceptionMapper.MalformedToolCall("LM Studio changed a tool call ID while streaming.");
                }

                _callId = callId;
            }

            if (!string.IsNullOrWhiteSpace(toolId))
            {
                if (_toolId is not null && !string.Equals(_toolId, toolId, StringComparison.Ordinal))
                {
                    throw LMStudioExceptionMapper.MalformedToolCall("LM Studio changed a tool name while streaming.");
                }

                _toolId = toolId;
            }

            if (!string.IsNullOrEmpty(argumentsDelta))
            {
                _arguments.Append(argumentsDelta);
            }
        }

        public bool TryBuild(out AgentToolCallRequest toolCall)
        {
            if (string.IsNullOrWhiteSpace(_toolId))
            {
                toolCall = null!;
                return false;
            }

            toolCall = new AgentToolCallRequest(
                string.IsNullOrWhiteSpace(_callId) ? Guid.NewGuid().ToString("N") : _callId,
                _toolId,
                _arguments.Length == 0 ? "{}" : _arguments.ToString());
            return true;
        }
    }
}

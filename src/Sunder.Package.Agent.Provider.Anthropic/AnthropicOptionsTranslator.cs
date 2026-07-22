using Anthropic.Models.Beta;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using BetaMessageCreateParams = Anthropic.Models.Beta.Messages.MessageCreateParams;
using BetaSpeed = Anthropic.Models.Beta.Messages.Speed;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal static class AnthropicOptionsTranslator
{
    private const int DefaultVisibleOutputTokens = 8192;
    private const int FallbackModelOutputLimit = 64000;
    private const int MinimumThinkingTokens = 1024;
    private const int MinimumMeaningfulVisibleTokens = 1024;

    internal static AnthropicRequest Translate(
        List<MessageParam> messages,
        ChatOptions? options,
        string modelId)
    {
        var includeTools = ShouldIncludeTools(options);
        var useAdaptiveThinking = UsesAdaptiveThinking(modelId);
        var hasVisibleReasoning = options?.Reasoning is
        {
            Effort: ReasoningEffort.Low or ReasoningEffort.Medium or ReasoningEffort.High or ReasoningEffort.ExtraHigh,
            Output: ReasoningOutput.Summary or ReasoningOutput.Full,
        };
        var budget = TranslateTokenBudget(options, modelId, allowExplicitThinking: !useAdaptiveThinking);
        if (includeTools
            && hasVisibleReasoning
            && options?.ToolMode is RequiredChatToolMode)
        {
            throw AnthropicExceptionMapper.UnsupportedThinkingToolChoice();
        }

        var parameters = new MessageCreateParams
        {
            MaxTokens = budget.MaxTokens,
            Messages = messages,
            Model = ProviderModelId.RemovePrefix(modelId, "anthropic"),
        };

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            parameters = parameters with { System = options.Instructions };
        }

        if (includeTools && options?.Tools is { Count: > 0 })
        {
            parameters = parameters with
            {
                Tools = AnthropicMessageTranslator.TranslateTools(options.Tools),
                ToolChoice = TranslateToolChoice(options),
            };
        }

        if (TranslateEffort(options?.Reasoning?.Effort) is { } effort)
        {
            parameters = parameters with { OutputConfig = new OutputConfig { Effort = effort } };
        }

        if (useAdaptiveThinking && hasVisibleReasoning)
        {
            parameters = parameters with
            {
                Thinking = new ThinkingConfigAdaptive
                {
                    Display = Display.Summarized,
                },
            };
        }
        else if (budget.ThinkingTokens is { } thinkingTokens)
        {
            parameters = parameters with
            {
                Thinking = new ThinkingConfigParam(
                    new ThinkingConfigEnabled
                    {
                        BudgetTokens = thinkingTokens,
                        Display = ThinkingConfigEnabledDisplay.Summarized,
                    },
                    null),
            };
        }

        return new AnthropicRequest(
            parameters,
            UsesFastMode(options),
            includeTools,
            options?.AllowMultipleToolCalls == true,
            budget);
    }

    internal static BetaMessageCreateParams TranslateFast(MessageCreateParams parameters)
        => BetaMessageCreateParams.FromRawUnchecked(
                parameters.RawHeaderData,
                parameters.RawQueryData,
                parameters.RawBodyData)
            with
        {
            Speed = BetaSpeed.Fast,
            Betas = [AnthropicBeta.FastMode2026_02_01],
        };

    internal static AnthropicTokenBudget TranslateTokenBudget(
        ChatOptions? options,
        string modelId,
        bool allowExplicitThinking = true)
    {
        var modelLimit = AnthropicModelCatalog.GetMaxOutputTokens(modelId) ?? FallbackModelOutputLimit;
        var requestedVisibleTokens = Math.Clamp(
            options?.MaxOutputTokens ?? DefaultVisibleOutputTokens,
            1,
            modelLimit);
        var desiredThinkingTokens = allowExplicitThinking
            ? TranslateThinkingTokens(options?.Reasoning)
            : null;
        if (desiredThinkingTokens is null)
        {
            return new AnthropicTokenBudget(requestedVisibleTokens, null, requestedVisibleTokens, modelLimit);
        }

        var maxTokens = Math.Min(modelLimit, (long)requestedVisibleTokens + desiredThinkingTokens.Value);
        var visibleReserve = Math.Min(requestedVisibleTokens, MinimumMeaningfulVisibleTokens);
        var thinkingTokens = Math.Min(desiredThinkingTokens.Value, maxTokens - visibleReserve);
        if (thinkingTokens < MinimumThinkingTokens)
        {
            return new AnthropicTokenBudget(requestedVisibleTokens, null, requestedVisibleTokens, modelLimit);
        }

        return new AnthropicTokenBudget(requestedVisibleTokens, thinkingTokens, maxTokens, modelLimit);
    }

    internal static Effort? TranslateEffort(ReasoningEffort? effort)
        => effort switch
        {
            ReasoningEffort.Low => Effort.Low,
            ReasoningEffort.Medium => Effort.Medium,
            ReasoningEffort.High => Effort.High,
            ReasoningEffort.ExtraHigh => Effort.Xhigh,
            _ => null,
        };

    private static int? TranslateThinkingTokens(ReasoningOptions? reasoning)
    {
        if (reasoning?.Output is not (ReasoningOutput.Summary or ReasoningOutput.Full))
        {
            return null;
        }

        return reasoning.Effort switch
        {
            ReasoningEffort.Low => 1024,
            ReasoningEffort.Medium => 2048,
            ReasoningEffort.High => 4096,
            ReasoningEffort.ExtraHigh => 8192,
            _ => null,
        };
    }

    private static bool UsesFastMode(ChatOptions? options)
        => options?.AdditionalProperties is { } properties
            && properties.TryGetValue(AgentChatModelOptionKeys.SpeedOptionId, out var value)
            && string.Equals(value as string, "fast", StringComparison.OrdinalIgnoreCase);

    private static bool UsesAdaptiveThinking(string modelId)
    {
        var normalized = ProviderModelId.RemovePrefix(modelId, "anthropic");
        return normalized.StartsWith("claude-fable-5", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("claude-sonnet-5", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("claude-opus-4-8", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("claude-opus-4-7", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("claude-opus-4-6", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("claude-sonnet-4-6", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldIncludeTools(ChatOptions? options)
    {
        var hasTools = options?.Tools is { Count: > 0 };
        return options?.ToolMode switch
        {
            null or AutoChatToolMode => hasTools,
            RequiredChatToolMode when !hasTools => throw AnthropicExceptionMapper.MalformedToolCall(
                "Anthropic tool mode requires a tool, but no tools were supplied."),
            RequiredChatToolMode => true,
            var mode when mode == ChatToolMode.None => false,
            _ => throw AnthropicExceptionMapper.UnsupportedToolMode(options?.ToolMode),
        };
    }

    private static ToolChoice TranslateToolChoice(ChatOptions options)
    {
        var disableParallelToolUse = options.AllowMultipleToolCalls != true;
        return options.ToolMode switch
        {
            null or AutoChatToolMode => new ToolChoiceAuto
            {
                DisableParallelToolUse = disableParallelToolUse,
            },
            RequiredChatToolMode { RequiredFunctionName: null or "" } => new ToolChoiceAny
            {
                DisableParallelToolUse = disableParallelToolUse,
            },
            RequiredChatToolMode required when options.Tools!.OfType<AIFunctionDeclaration>()
                .Any(tool => string.Equals(tool.Name, required.RequiredFunctionName, StringComparison.Ordinal)) => new ToolChoiceTool
                {
                    Name = required.RequiredFunctionName!,
                    DisableParallelToolUse = disableParallelToolUse,
                },
            RequiredChatToolMode required => throw AnthropicExceptionMapper.MalformedToolCall(
                $"Anthropic required tool '{required.RequiredFunctionName}' was not supplied."),
            _ => throw AnthropicExceptionMapper.UnsupportedToolMode(options.ToolMode),
        };
    }
}

internal sealed record AnthropicRequest(
    MessageCreateParams Parameters,
    bool UseFastMode,
    bool IncludeTools,
    bool AllowMultipleToolCalls,
    AnthropicTokenBudget TokenBudget);

internal readonly record struct AnthropicTokenBudget(
    int RequestedVisibleTokens,
    long? ThinkingTokens,
    long MaxTokens,
    int ModelLimit);

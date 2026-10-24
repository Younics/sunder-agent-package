using Anthropic.Models.Beta;
using Anthropic.Models.Messages;
using System.Text.Json;
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
        var model = AnthropicModelCatalog.Find(modelId);
        AgentChatModelOptions.Validate(options, modelId, model?.Variants?.Select(v => v.VariantId), model?.SpeedOptions?.Select(v => v.SpeedOptionId));
        var reasoningId = AgentChatModelOptions.ReasoningId(options);
        var includeTools = ShouldIncludeTools(options);
        var useAdaptiveThinking = AnthropicModelCatalog.UsesAdaptiveThinking(modelId);
        var hasVisibleReasoning = reasoningId is not null and not "none"
                                  && options?.Reasoning?.Output != ReasoningOutput.None;
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

        if (AnthropicModelCatalog.SupportsNativeEffort(modelId) && TranslateEffort(reasoningId) is { } effort)
        {
            parameters = parameters with { OutputConfig = new OutputConfig { Effort = effort } };
        }

        if (useAdaptiveThinking && reasoningId == "none")
        {
            parameters = parameters with { Thinking = new ThinkingConfigDisabled() };
        }
        else if (useAdaptiveThinking && hasVisibleReasoning)
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

    internal static MessageCreateParams TranslateStableFast(MessageCreateParams parameters)
    {
        var body = parameters.RawBodyData.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        body["speed"] = JsonSerializer.SerializeToElement("fast");
        return MessageCreateParams.FromRawUnchecked(parameters.RawHeaderData, parameters.RawQueryData, body);
    }

    internal static AnthropicTokenBudget TranslateTokenBudget(
        ChatOptions? options,
        string modelId,
        bool allowExplicitThinking = true)
    {
        var modelLimit = AnthropicModelCatalog.GetMaxOutputTokens(modelId) ?? FallbackModelOutputLimit;
        // Adaptive thinking shares max_tokens with the answer. Keep explicit
        // caller caps, but do not give Max the legacy 8K total by default.
        var defaultTokens = AnthropicModelCatalog.UsesAdaptiveThinking(modelId)
            ? AgentChatModelOptions.ReasoningId(options) switch
            {
                "none" or "low" => DefaultVisibleOutputTokens,
                "medium" => 16384,
                "xhigh" => 65536,
                "max" => modelLimit,
                _ => 32768,
            } : DefaultVisibleOutputTokens;
        var requestedVisibleTokens = Math.Clamp(
            options?.MaxOutputTokens ?? defaultTokens,
            1,
            modelLimit);
        var desiredThinkingTokens = allowExplicitThinking
            ? TranslateThinkingTokens(options)
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

    private static Effort? TranslateEffort(string? effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => null,
    };

    private static int? TranslateThinkingTokens(ChatOptions? options)
    {
        if (options?.Reasoning?.Output == ReasoningOutput.None)
        {
            return null;
        }

        return AgentChatModelOptions.ReasoningId(options) switch
        {
            "low" => 1024,
            "medium" => 2048,
            "high" => 4096,
            "xhigh" => 8192,
            _ => null,
        };
    }

    private static bool UsesFastMode(ChatOptions? options)
        => AgentChatModelOptions.SpeedId(options) == "fast";

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

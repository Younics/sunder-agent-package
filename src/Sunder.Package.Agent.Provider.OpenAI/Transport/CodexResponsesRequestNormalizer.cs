using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class CodexResponsesRequestNormalizer
{
    public static CodexNormalizedResponsesRequest Normalize(
        AgentChatClientContext context,
        IReadOnlyList<AIChatMessage> messages,
        ChatOptions? options,
        bool toolAware,
        string? promptCacheKeyOverride = null)
    {
        var modelId = options?.ModelId ?? context.ModelId;
        var model = OpenAiModelIds.Normalize(modelId);
        var modelCapabilities = OpenAiModelCatalog.GetCapabilities(model);
        var isReasoningModel = modelCapabilities.SupportsReasoning;
        var conversationInput = CodexResponsesInputNormalizer.BuildNativeInput(messages, isReasoningModel);
        var hasDeclaredTools = options?.Tools is { Count: > 0 }
                               && (!modelCapabilities.UseResponsesLite || toolAware);
        var tools = hasDeclaredTools
            ? CodexResponsesInputNormalizer.BuildFunctionTools(options?.Tools ?? [])
            : [];
        var promptCacheKey = !string.IsNullOrWhiteSpace(promptCacheKeyOverride)
            ? promptCacheKeyOverride
            : string.IsNullOrWhiteSpace(options?.ConversationId) ? null : options.ConversationId;
        var instructions = string.IsNullOrWhiteSpace(options?.Instructions) ? null : options.Instructions;
        IReadOnlyList<string>? include = isReasoningModel ? ["reasoning.encrypted_content"] : null;
        var reasoning = BuildReasoningOptions(modelCapabilities, options?.Reasoning);
        var input = modelCapabilities.UseResponsesLite
            ? BuildResponsesLiteInput(conversationInput, instructions, tools)
            : conversationInput;
        var usesDeveloperInstructionInput = CodexResponsesInputNormalizer.UsesDeveloperInstructionInput(messages, isReasoningModel)
                                            || modelCapabilities.UseResponsesLite && instructions is not null;

        return new CodexNormalizedResponsesRequest(
            model,
            input,
            modelCapabilities.UseResponsesLite ? string.Empty : instructions,
            tools,
            modelCapabilities.UseResponsesLite ? "auto" : BuildToolChoice(options, tools, hasDeclaredTools),
            modelCapabilities.UseResponsesLite
                ? false
                : hasDeclaredTools ? options?.AllowMultipleToolCalls == true : null,
            promptCacheKey,
            include,
            GetServiceTier(modelId, options),
            MaxOutputTokens: null,
            reasoning,
            modelCapabilities.UseLowTextVerbosity ? new CodexTextOptions("low") : null,
            usesDeveloperInstructionInput,
            modelCapabilities.UseResponsesLite);
    }

    private static IReadOnlyList<object> BuildResponsesLiteInput(
        IReadOnlyList<object> conversationInput,
        string? instructions,
        IReadOnlyList<object> tools)
    {
        var input = new List<object>(conversationInput.Count + 2)
        {
            new CodexAdditionalToolsInput(tools),
        };
        if (instructions is not null)
        {
            input.Add(new CodexDeveloperMessageInput([new CodexTextPart("input_text", instructions)]));
        }

        input.AddRange(conversationInput);
        return input;
    }

    private static object? BuildToolChoice(
        ChatOptions? options,
        IReadOnlyList<object> tools,
        bool hasDeclaredTools)
    {
        if (!hasDeclaredTools)
        {
            return options?.ToolMode switch
            {
                null or AutoChatToolMode => null,
                RequiredChatToolMode => throw CreateToolModeException(
                    "OpenAI tool mode requires a tool, but no tools were supplied."),
                var mode when mode == ChatToolMode.None => null,
                _ => throw CreateToolModeException(
                    $"OpenAI does not support tool mode '{options?.ToolMode?.GetType().Name ?? "null"}'."),
            };
        }

        return options?.ToolMode switch
        {
            null or AutoChatToolMode => "auto",
            RequiredChatToolMode { RequiredFunctionName: null or "" } => "required",
            RequiredChatToolMode required when tools.OfType<CodexFunctionTool>().Any(tool => string.Equals(
                tool.Name,
                required.RequiredFunctionName,
                StringComparison.Ordinal)) => new CodexRequiredFunctionToolChoice(required.RequiredFunctionName!),
            RequiredChatToolMode required => throw CreateToolModeException(
                $"Required OpenAI tool '{required.RequiredFunctionName}' was not supplied."),
            var mode when mode == ChatToolMode.None => "none",
            _ => throw CreateToolModeException(
                $"OpenAI does not support tool mode '{options?.ToolMode?.GetType().Name ?? "null"}'."),
        };
    }

    private static AgentChatProviderException CreateToolModeException(string detail)
        => new(
            detail,
            $"### Unsupported OpenAI tool mode\n\n{detail}",
            "openai-unsupported-tool-mode");

    private static CodexReasoningOptions? BuildReasoningOptions(
        OpenAiModelCapabilities modelCapabilities,
        ReasoningOptions? reasoning)
    {
        if (!modelCapabilities.SupportsReasoning)
        {
            return null;
        }

        var summary = reasoning?.Output == ReasoningOutput.None ? null : "auto";

        // The Codex-connected endpoint rejects reasoning.mode. API-key mode applies Pro in OpenAiModelOptionsChatClient.
        return new CodexReasoningOptions(
            ToOpenAiReasoningEffort(reasoning?.Effort) ?? modelCapabilities.DefaultReasoningEffort,
            summary,
            Mode: null,
            Context: modelCapabilities.UseResponsesLite ? "all_turns" : null);
    }

    private static string? ToOpenAiReasoningEffort(ReasoningEffort? effort)
        => effort switch
        {
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "xhigh",
            _ => null,
        };

    private static string? GetServiceTier(string modelId, ChatOptions? options)
        => string.Equals(GetModelOption(options, AgentChatModelOptionKeys.SpeedOptionId), "fast", StringComparison.OrdinalIgnoreCase)
           || NormalizeModelVariantId(modelId).EndsWith("-fast", StringComparison.OrdinalIgnoreCase)
            ? "priority"
            : null;

    private static string? GetModelOption(ChatOptions? options, string key)
        => options?.AdditionalProperties is { } properties
           && properties.TryGetValue(key, out var value)
            ? value as string
            : null;

    private static string NormalizeModelVariantId(string modelId)
    {
        const string openAiPrefix = "openai/";
        return modelId.StartsWith(openAiPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[openAiPrefix.Length..]
            : modelId;
    }
}

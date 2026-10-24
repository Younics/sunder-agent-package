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
        var descriptor = OpenAiModelCatalog.Find(model, connected: true);
        if (descriptor is null)
            throw new AgentChatProviderException("The selected model is unavailable on this ChatGPT connection.",
                "Choose a model supported by the ChatGPT connection or switch to API-key authentication.", "openai-model-unavailable");
        AgentChatModelOptions.Validate(options, modelId, descriptor.Variants?.Select(v => v.VariantId),
            descriptor.SpeedOptions?.Select(v => v.SpeedOptionId), descriptor.ModeOptions?.Select(v => v.ModeOptionId));
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
        var reasoning = BuildReasoningOptions(modelCapabilities, options);
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
            GetServiceTier(options),
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
        ChatOptions? options)
    {
        if (!modelCapabilities.SupportsReasoning)
        {
            return null;
        }

        var effort = AgentChatModelOptions.ReasoningId(options);
        var summary = effort == "none" || options?.Reasoning?.Output == ReasoningOutput.None ? null : "auto";

        // The Codex-connected endpoint rejects reasoning.mode. API-key mode applies Pro in OpenAiModelOptionsChatClient.
        return new CodexReasoningOptions(
            effort,
            summary,
            Mode: null,
            Context: modelCapabilities.UseResponsesLite ? "all_turns" : null);
    }

    private static string? GetServiceTier(ChatOptions? options)
        => AgentChatModelOptions.SpeedId(options) switch { "fast" => "priority", "normal" => "default", _ => null };

    private static string? GetModelOption(ChatOptions? options, string key)
        => options?.AdditionalProperties is { } properties
           && properties.TryGetValue(key, out var value)
            ? value as string
            : null;

}

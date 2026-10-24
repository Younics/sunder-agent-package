using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatTool = Microsoft.Extensions.AI.AITool;
using AIChatToolMode = Microsoft.Extensions.AI.ChatToolMode;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.LMStudio;

#pragma warning disable OPENAI001 // The adapter owns the SDK's experimental reasoning-field boundary.

internal static class LMStudioOpenAIOptionsTranslator
{
    public static ChatCompletionOptions Translate(ChatOptions? options)
    {
        var translated = new ChatCompletionOptions
        {
            AllowParallelToolCalls = options?.AllowMultipleToolCalls == true,
        };
        var reasoning = AgentChatModelOptions.ReasoningId(options);
        AgentChatModelOptions.Validate(options, options?.ModelId ?? "LM Studio", ["low", "medium", "high"], null);
        if (reasoning is not null)
        {
            translated.ReasoningEffortLevel = new ChatReasoningEffortLevel(reasoning);
        }

        if (options?.MaxOutputTokens is { } maxOutputTokens)
        {
            translated.MaxOutputTokenCount = maxOutputTokens;
        }

        if (options?.ToolMode == AIChatToolMode.None)
        {
            translated.ToolChoice = ChatToolChoice.CreateNoneChoice();
            return translated;
        }

        foreach (var tool in TranslateTools(options?.Tools ?? []))
        {
            translated.Tools.Add(tool);
        }

        if (options?.ToolMode is RequiredChatToolMode required)
        {
            translated.ToolChoice = string.IsNullOrWhiteSpace(required.RequiredFunctionName)
                ? ChatToolChoice.CreateRequiredChoice()
                : ChatToolChoice.CreateFunctionChoice(required.RequiredFunctionName);
        }

        return translated;
    }

    private static IReadOnlyList<ChatTool> TranslateTools(IList<AIChatTool> tools)
        => tools
            .OfType<AIFunctionDeclaration>()
            .Select(tool => ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(BuildToolSchemaJson(tool.JsonSchema))))
            .ToArray();

    private static string BuildToolSchemaJson(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false,
            })
            : schema.GetRawText();
}

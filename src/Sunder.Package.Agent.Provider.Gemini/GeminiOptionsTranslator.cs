using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using GenAIContent = Google.GenAI.Types.Content;
using GenAITool = Google.GenAI.Types.Tool;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiOptionsTranslator
{
    public GenerateContentConfig Translate(
        ChatOptions? options,
        bool includeTools,
        GenAIContent? systemInstruction,
        string modelId)
    {
        var config = new GenerateContentConfig
        {
            SystemInstruction = MergeSystemInstructions(options?.Instructions, systemInstruction),
            MaxOutputTokens = options?.MaxOutputTokens,
            ThinkingConfig = TranslateThinking(options?.Reasoning, modelId),
        };

        if (includeTools && options?.Tools is { Count: > 0 })
        {
            var declarations = new List<FunctionDeclaration>(options.Tools.Count);
            foreach (var tool in options.Tools)
            {
                if (tool is not AIFunctionDeclaration function)
                {
                    throw GeminiExceptionMapper.UnsupportedTool(tool);
                }

                declarations.Add(new FunctionDeclaration
                {
                    Name = function.Name,
                    Description = function.Description,
                    ParametersJsonSchema = TranslateSchema(function.JsonSchema),
                });
            }

            config.Tools = [new GenAITool { FunctionDeclarations = declarations }];
            config.ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = TranslateToolMode(options, declarations),
                    AllowedFunctionNames = GetAllowedFunctionNames(options),
                },
            };
        }

        return config;
    }

    private static GenAIContent? MergeSystemInstructions(
        string? instructions,
        GenAIContent? systemInstruction)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return systemInstruction;
        }

        var parts = new List<Part> { new() { Text = instructions } };
        if (systemInstruction?.Parts is { Count: > 0 })
        {
            parts.AddRange(systemInstruction.Parts);
        }

        return new GenAIContent { Parts = parts };
    }

    internal static bool ShouldIncludeTools(ChatOptions? options)
    {
        var hasTools = options?.Tools is { Count: > 0 };
        return options?.ToolMode switch
        {
            null or AutoChatToolMode => hasTools,
            RequiredChatToolMode when !hasTools => throw GeminiExceptionMapper.MalformedToolCall(
                "Gemini tool mode requires a tool, but no tools were supplied."),
            RequiredChatToolMode => true,
            var mode when mode == ChatToolMode.None => false,
            _ => throw GeminiExceptionMapper.UnsupportedToolMode(options?.ToolMode),
        };
    }

    private static ThinkingConfig? TranslateThinking(ReasoningOptions? reasoning, string modelId)
    {
        if (reasoning?.Effort is not { } effort)
        {
            return null;
        }

        var includeThoughts = reasoning?.Output is ReasoningOutput.Summary or ReasoningOutput.Full;
        var normalizedModelId = modelId.StartsWith("gemini/", StringComparison.OrdinalIgnoreCase)
            ? modelId["gemini/".Length..]
            : modelId;
        if (normalizedModelId.StartsWith("gemini-2.5-", StringComparison.OrdinalIgnoreCase))
        {
            var maxBudget = normalizedModelId.Equals("gemini-2.5-pro", StringComparison.OrdinalIgnoreCase)
                ? 32768
                : 24576;
            var supportsDisabledThinking = normalizedModelId.Equals("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase);
            var budget = effort switch
            {
                ReasoningEffort.None when supportsDisabledThinking => 0,
                ReasoningEffort.None => (int?)null,
                ReasoningEffort.Low => 1024,
                ReasoningEffort.Medium => 8192,
                ReasoningEffort.High or ReasoningEffort.ExtraHigh => maxBudget,
                _ => null,
            };
            return budget is null
                ? null
                : new ThinkingConfig { ThinkingBudget = budget, IncludeThoughts = includeThoughts };
        }

        if (normalizedModelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
        {
            return effort switch
            {
                ReasoningEffort.Low => new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = includeThoughts },
                ReasoningEffort.Medium => new ThinkingConfig { ThinkingLevel = ThinkingLevel.Medium, IncludeThoughts = includeThoughts },
                ReasoningEffort.High or ReasoningEffort.ExtraHigh => new ThinkingConfig { ThinkingLevel = ThinkingLevel.High, IncludeThoughts = includeThoughts },
                _ => throw GeminiExceptionMapper.UnsupportedReasoning(modelId, effort),
            };
        }

        throw GeminiExceptionMapper.UnsupportedReasoning(modelId, effort);
    }

    private static FunctionCallingConfigMode TranslateToolMode(
        ChatOptions? options,
        IReadOnlyList<FunctionDeclaration> declarations)
        => options?.ToolMode switch
        {
            null or AutoChatToolMode => FunctionCallingConfigMode.Auto,
            RequiredChatToolMode { RequiredFunctionName: null or "" } => FunctionCallingConfigMode.Any,
            RequiredChatToolMode required when declarations.Any(declaration => string.Equals(
                declaration.Name,
                required.RequiredFunctionName,
                StringComparison.Ordinal)) => FunctionCallingConfigMode.Any,
            RequiredChatToolMode required => throw GeminiExceptionMapper.MalformedToolCall(
                $"Gemini required tool '{required.RequiredFunctionName}' was not supplied."),
            _ => throw GeminiExceptionMapper.UnsupportedToolMode(options?.ToolMode),
        };

    private static List<string>? GetAllowedFunctionNames(ChatOptions? options)
        => options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } functionName }
            ? [functionName]
            : null;

    private static JsonElement TranslateSchema(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false,
            })
            : schema.Clone();
}

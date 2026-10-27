using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using GenAIContent = Google.GenAI.Types.Content;
using Sunder.Package.Agent.Contracts.Models;
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
        var model = GeminiModelCatalog.Find(modelId);
        AgentChatModelOptions.Validate(options, modelId, model?.Variants?.Select(v => v.VariantId), model?.SpeedOptions?.Select(v => v.SpeedOptionId));
        var reasoningId = AgentChatModelOptions.ReasoningId(options);
        var maxOutput = options?.MaxOutputTokens;
        if (GeminiModelCatalog.IsArgon(modelId))
            maxOutput = Math.Min(maxOutput ?? 32768, includeTools ? 32768 : 65536);
        var config = new GenerateContentConfig
        {
            // The Agent owns retries and partial-output continuation so every
            // request is visible, cancellable and accounted for exactly once.
            AutomaticContinuation = false,
            HttpOptions = new HttpOptions { RetryOptions = new HttpRetryOptions { Attempts = 1 } },
            SystemInstruction = MergeSystemInstructions(options?.Instructions, systemInstruction),
            MaxOutputTokens = maxOutput,
            ThinkingConfig = TranslateThinking(reasoningId, options?.Reasoning?.Output, modelId),
            ServiceTier = AgentChatModelOptions.SpeedId(options) switch { "fast" => ServiceTier.Priority, "normal" => ServiceTier.Standard, _ => (ServiceTier?)null },
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

    private static ThinkingConfig? TranslateThinking(string? effort, ReasoningOutput? output, string modelId)
    {
        if (effort is null)
        {
            return null;
        }

        var includeThoughts = output is null or ReasoningOutput.Summary or ReasoningOutput.Full;
        var normalizedModelId = modelId.StartsWith("gemini/", StringComparison.OrdinalIgnoreCase)
            ? modelId["gemini/".Length..]
            : modelId;
        if (normalizedModelId.StartsWith("gemini-2.5-", StringComparison.OrdinalIgnoreCase))
        {
            var maxBudget = normalizedModelId.Equals("gemini-2.5-pro", StringComparison.OrdinalIgnoreCase)
                ? 32768
                : 24576;
            var supportsDisabledThinking = normalizedModelId.Equals("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase);
            int? budget = effort switch
            {
                "none" when supportsDisabledThinking => 0,
                "low" => 1024,
                "medium" => 8192,
                "high" => maxBudget,
                _ => null,
            };
            return budget is null
                ? null
                : new ThinkingConfig { ThinkingBudget = budget, IncludeThoughts = includeThoughts };
        }

        if (normalizedModelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) || GeminiModelCatalog.IsArgon(modelId))
        {
            return effort switch
            {
                "minimal" => new ThinkingConfig { ThinkingLevel = ThinkingLevel.Minimal, IncludeThoughts = includeThoughts },
                "low" => new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = includeThoughts },
                "medium" => new ThinkingConfig { ThinkingLevel = ThinkingLevel.Medium, IncludeThoughts = includeThoughts },
                "high" => new ThinkingConfig { ThinkingLevel = ThinkingLevel.High, IncludeThoughts = includeThoughts },
                _ => throw new AgentChatProviderException($"Unsupported Gemini reasoning '{effort}'.", $"Unsupported Gemini reasoning '{effort}'.", "gemini-unsupported-reasoning"),
            };
        }

        throw new AgentChatProviderException($"Unsupported Gemini reasoning '{effort}'.", $"Unsupported Gemini reasoning '{effort}'.", "gemini-unsupported-reasoning");
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

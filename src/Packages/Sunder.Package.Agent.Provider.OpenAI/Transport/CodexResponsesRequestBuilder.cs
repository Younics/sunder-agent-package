using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using AIChatTool = Microsoft.Extensions.AI.AITool;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class CodexResponsesRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CodexResponsesRequest Build(
        AgentChatClientContext context,
        IReadOnlyList<AIChatMessage> messages,
        ChatOptions? options,
        bool toolAware)
    {
        var modelId = options?.ModelId ?? context.ModelId;
        var model = OpenAiModelIds.Normalize(modelId);
        var serviceTier = GetServiceTier(modelId);
        var isReasoningModel = IsReasoningModel(model);
        var input = BuildNativeInput(messages, isReasoningModel);
        var tools = toolAware ? BuildFunctionTools(options?.Tools ?? []) : [];
        var promptCacheKey = string.IsNullOrWhiteSpace(options?.ConversationId) ? null : options.ConversationId;
        var instructions = string.IsNullOrWhiteSpace(options?.Instructions) ? null : options.Instructions;
        IReadOnlyList<string>? include = isReasoningModel ? ["reasoning.encrypted_content"] : null;
        var toolChoice = toolAware ? "auto" : null;
        var body = new CodexResponsesRequestBody
        {
            Model = model,
            Input = input,
            Instructions = instructions,
            Tools = toolAware ? tools : null,
            ToolChoice = toolChoice,
            Stream = true,
            Store = false,
            PromptCacheKey = promptCacheKey,
            Include = include,
            ServiceTier = serviceTier,
            Reasoning = BuildReasoningOptions(isReasoningModel, options?.Reasoning),
            Text = ShouldUseLowTextVerbosity(model) ? new CodexTextOptions("low") : null,
        };
        var bodyJson = JsonSerializer.Serialize(body, JsonOptions);

        return new CodexResponsesRequest(
            model,
            input.Count,
            tools.Count,
            bodyJson,
            UsesDeveloperInstructionInput: UsesDeveloperInstructionInput(messages, isReasoningModel),
            HasTopLevelInstructions: instructions is not null,
            ServiceTier: serviceTier,
            HasPromptCacheKey: !string.IsNullOrWhiteSpace(promptCacheKey),
            HasIncludeOptions: include is { Count: > 0 },
            HasReasoningOptions: body.Reasoning is not null,
            HasTextOptions: body.Text is not null,
            ToolChoice: toolChoice);
    }

    private static IReadOnlyList<object> BuildNativeInput(
        IEnumerable<AIChatMessage> messages,
        bool useDeveloperInstructions)
    {
        var input = new List<object>();
        var functionCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            AddNativeInput(input, message, useDeveloperInstructions, functionCallIds);
        }

        return input;
    }

    private static void AddNativeInput(
        ICollection<object> input,
        AIChatMessage message,
        bool useDeveloperInstructions,
        ISet<string> functionCallIds)
    {
        var textBuilder = new StringBuilder();
        var userContentParts = message.Role == AIChatRole.User ? new List<object>() : null;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    AppendText(textBuilder, textContent.Text);
                    break;

                case DataContent dataContent when userContentParts is not null && TryBuildAttachmentPart(dataContent, out var attachmentPart):
                    FlushTextPart(userContentParts, textBuilder);
                    userContentParts.Add(attachmentPart);
                    break;

                case FunctionCallContent functionCall:
                    FlushInput(input, message.Role, textBuilder, userContentParts, useDeveloperInstructions);
                    functionCallIds.Add(functionCall.CallId);
                    input.Add(new CodexFunctionCallInput(
                        functionCall.CallId,
                        functionCall.Name,
                        SerializeArguments(functionCall.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal))));
                    break;

                case FunctionResultContent functionResult when functionCallIds.Contains(functionResult.CallId):
                    FlushInput(input, message.Role, textBuilder, userContentParts, useDeveloperInstructions);
                    input.Add(new CodexFunctionCallOutputInput(functionResult.CallId, RenderFunctionResult(functionResult.Result)));
                    break;
            }
        }

        if (textBuilder.Length == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            textBuilder.Append(message.Text);
        }

        FlushInput(input, message.Role, textBuilder, userContentParts, useDeveloperInstructions);
    }

    private static void FlushInput(
        ICollection<object> input,
        AIChatRole role,
        StringBuilder textBuilder,
        List<object>? userContentParts,
        bool useDeveloperInstructions)
    {
        if (userContentParts is null)
        {
            FlushTextInput(input, role, textBuilder, useDeveloperInstructions);
            return;
        }

        FlushTextPart(userContentParts, textBuilder);
        if (userContentParts.Count == 0)
        {
            return;
        }

        input.Add(new CodexUserTextInput(userContentParts.ToArray()));
        userContentParts.Clear();
    }

    private static void FlushTextPart(ICollection<object> contentParts, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        contentParts.Add(new CodexTextPart("input_text", textBuilder.ToString()));
        textBuilder.Clear();
    }

    private static void FlushTextInput(
        ICollection<object> input,
        AIChatRole role,
        StringBuilder textBuilder,
        bool useDeveloperInstructions)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        var text = textBuilder.ToString();
        if (role == AIChatRole.System)
        {
            input.Add(new CodexInstructionInput(useDeveloperInstructions ? "developer" : "system", text));
        }
        else if (role == AIChatRole.Assistant)
        {
            input.Add(new CodexAssistantTextInput([new CodexTextPart("output_text", text)]));
        }
        else
        {
            input.Add(new CodexUserTextInput([new CodexTextPart("input_text", text)]));
        }

        textBuilder.Clear();
    }

    private static IReadOnlyList<object> BuildFunctionTools(IList<AIChatTool> tools)
        => tools
            .OfType<AIFunctionDeclaration>()
            .Select(tool => new CodexFunctionTool(
                tool.Name,
                tool.Description,
                OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(BuildToolSchemaJson(tool.JsonSchema)),
                Strict: false))
            .ToArray();

    private static string? BuildToolSchemaJson(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Undefined
            ? null
            : schema.GetRawText();

    private static bool UsesDeveloperInstructionInput(IEnumerable<AIChatMessage> messages, bool useDeveloperInstructions)
        => useDeveloperInstructions && messages.Any(message => message.Role == AIChatRole.System && HasText(message));

    private static bool HasText(AIChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            return true;
        }

        return message.Contents.OfType<TextContent>().Any(content => !string.IsNullOrWhiteSpace(content.Text));
    }

    private static bool IsReasoningModel(string model)
        => model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
           && !model.StartsWith("gpt-5-chat", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("codex-", StringComparison.OrdinalIgnoreCase)
           || model.Contains("-codex", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseLowTextVerbosity(string model)
        => model.StartsWith("gpt-5.", StringComparison.OrdinalIgnoreCase)
           && !model.Contains("codex", StringComparison.OrdinalIgnoreCase)
           && !model.Contains("-chat", StringComparison.OrdinalIgnoreCase);

    private static CodexReasoningOptions? BuildReasoningOptions(bool isReasoningModel, ReasoningOptions? reasoning)
    {
        if (!isReasoningModel)
        {
            return null;
        }

        return new CodexReasoningOptions(ToOpenAiReasoningEffort(reasoning?.Effort) ?? "medium", "auto");
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

    private static string? GetServiceTier(string modelId)
        => NormalizeModelVariantId(modelId).EndsWith("-fast", StringComparison.OrdinalIgnoreCase)
            ? "priority"
            : null;

    private static string NormalizeModelVariantId(string modelId)
    {
        const string openAiPrefix = "openai/";
        return modelId.StartsWith(openAiPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[openAiPrefix.Length..]
            : modelId;
    }

    private static void AppendText(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(text);
    }

    private static string SerializeArguments(IDictionary<string, object?> arguments)
        => arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments);

    private static string RenderFunctionResult(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };

    private sealed class CodexResponsesRequestBody
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required IReadOnlyList<object> Input { get; init; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<object>? Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; init; }

        [JsonPropertyName("stream")]
        public required bool Stream { get; init; }

        [JsonPropertyName("store")]
        public required bool Store { get; init; }

        [JsonPropertyName("prompt_cache_key")]
        public string? PromptCacheKey { get; init; }

        [JsonPropertyName("include")]
        public IReadOnlyList<string>? Include { get; init; }

        [JsonPropertyName("service_tier")]
        public string? ServiceTier { get; init; }

        [JsonPropertyName("reasoning")]
        public CodexReasoningOptions? Reasoning { get; init; }

        [JsonPropertyName("text")]
        public CodexTextOptions? Text { get; init; }
    }

    private sealed record CodexInstructionInput(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private static bool TryBuildAttachmentPart(DataContent dataContent, out object attachmentPart)
    {
        var mediaType = dataContent.MediaType ?? string.Empty;
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            attachmentPart = new CodexImagePart(dataContent.Uri);
            return true;
        }

        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            attachmentPart = new CodexFilePart(
                string.IsNullOrWhiteSpace(dataContent.Name) ? "attachment.pdf" : dataContent.Name,
                dataContent.Uri);
            return true;
        }

        attachmentPart = null!;
        return false;
    }

    private sealed record CodexUserTextInput(
        [property: JsonPropertyName("content")] IReadOnlyList<object> Content)
    {
        [JsonPropertyName("role")]
        public string Role { get; } = "user";
    }

    private sealed record CodexAssistantTextInput(
        [property: JsonPropertyName("content")] IReadOnlyList<CodexTextPart> Content)
    {
        [JsonPropertyName("role")]
        public string Role { get; } = "assistant";
    }

    private sealed record CodexTextPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);

    private sealed record CodexImagePart(
        [property: JsonPropertyName("image_url")] string ImageUrl)
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "input_image";
    }

    private sealed record CodexFilePart(
        [property: JsonPropertyName("filename")] string FileName,
        [property: JsonPropertyName("file_data")] string FileData)
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "input_file";
    }

    private sealed record CodexFunctionCallInput(
        [property: JsonPropertyName("call_id")] string CallId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments)
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "function_call";
    }

    private sealed record CodexFunctionCallOutputInput(
        [property: JsonPropertyName("call_id")] string CallId,
        [property: JsonPropertyName("output")] string Output)
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "function_call_output";
    }

    private sealed record CodexFunctionTool(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters,
        [property: JsonPropertyName("strict")] bool Strict)
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "function";
    }

    private sealed record CodexReasoningOptions(
        [property: JsonPropertyName("effort")] string Effort,
        [property: JsonPropertyName("summary")] string Summary);

    private sealed record CodexTextOptions(
        [property: JsonPropertyName("verbosity")] string Verbosity);
}

internal sealed record CodexResponsesRequest(
    string Model,
    int InputItemCount,
    int ToolCount,
    string Body,
    bool UsesDeveloperInstructionInput,
    bool HasTopLevelInstructions,
    string? ServiceTier,
    bool HasPromptCacheKey,
    bool HasIncludeOptions,
    bool HasReasoningOptions,
    bool HasTextOptions,
    string? ToolChoice);

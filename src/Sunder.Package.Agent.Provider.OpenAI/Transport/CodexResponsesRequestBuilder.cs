using System.Security.Cryptography;
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
        bool toolAware,
        CodexResponseContinuationState? continuationState = null,
        bool disableContinuation = false)
    {
        var modelId = options?.ModelId ?? context.ModelId;
        var model = OpenAiModelIds.Normalize(modelId);
        var serviceTier = GetServiceTier(modelId, options);
        var isReasoningModel = IsReasoningModel(model);
        var input = BuildNativeInput(messages, isReasoningModel);
        var conversationItemFingerprints = BuildItemFingerprints(input);
        var tools = toolAware ? BuildFunctionTools(options?.Tools ?? []) : [];
        var promptCacheKey = string.IsNullOrWhiteSpace(options?.ConversationId) ? null : options.ConversationId;
        var instructions = string.IsNullOrWhiteSpace(options?.Instructions) ? null : options.Instructions;
        IReadOnlyList<string>? include = isReasoningModel ? ["reasoning.encrypted_content"] : null;
        var toolChoice = toolAware ? "auto" : null;
        bool? parallelToolCalls = toolAware ? options?.AllowMultipleToolCalls == true : null;
        var reasoning = BuildReasoningOptions(isReasoningModel, options?.Reasoning);
        var text = ShouldUseLowTextVerbosity(model) ? new CodexTextOptions("low") : null;
        var shapeFingerprint = BuildShapeFingerprint(model, instructions, tools, toolChoice, parallelToolCalls, include, serviceTier, reasoning, text);
        var previousResponseId = TryBuildContinuationInput(
            continuationState,
            shapeFingerprint,
            conversationItemFingerprints,
            input,
            disableContinuation,
            out var requestInput)
            ? continuationState!.ResponseId
            : null;
        var body = new CodexResponsesRequestBody
        {
            Model = model,
            Input = requestInput,
            Instructions = instructions,
            Tools = toolAware ? tools : null,
            ToolChoice = toolChoice,
            ParallelToolCalls = parallelToolCalls,
            Stream = true,
            Store = false,
            PreviousResponseId = previousResponseId,
            PromptCacheKey = promptCacheKey,
            Include = include,
            ServiceTier = serviceTier,
            Reasoning = reasoning,
            Text = text,
        };
        var bodyJson = JsonSerializer.Serialize(body, JsonOptions);

        return new CodexResponsesRequest(
            model,
            requestInput.Count,
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
            ToolChoice: toolChoice,
            ParallelToolCalls: parallelToolCalls,
            HasPreviousResponseId: previousResponseId is not null,
            ShapeFingerprint: shapeFingerprint,
            ConversationItemFingerprints: conversationItemFingerprints);
    }

    public static IReadOnlyList<string> BuildAssistantOutputFingerprints(
        string? text,
        IReadOnlyList<FunctionCallContent> functionCalls)
    {
        var output = new List<object>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            output.Add(new CodexAssistantTextInput([new CodexTextPart("output_text", text)]));
        }

        foreach (var functionCall in functionCalls)
        {
            output.Add(new CodexFunctionCallInput(
                functionCall.CallId,
                functionCall.Name,
                SerializeArguments(functionCall.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal))));
        }

        return BuildItemFingerprints(output);
    }

    private static bool TryBuildContinuationInput(
        CodexResponseContinuationState? continuationState,
        string shapeFingerprint,
        IReadOnlyList<string> conversationItemFingerprints,
        IReadOnlyList<object> input,
        bool disableContinuation,
        out IReadOnlyList<object> requestInput)
    {
        requestInput = input;
        if (disableContinuation
            || continuationState is null
            || !string.Equals(continuationState.ShapeFingerprint, shapeFingerprint, StringComparison.Ordinal)
            || continuationState.ConversationItemFingerprints.Count >= conversationItemFingerprints.Count)
        {
            return false;
        }

        for (var index = 0; index < continuationState.ConversationItemFingerprints.Count; index++)
        {
            if (!string.Equals(
                    continuationState.ConversationItemFingerprints[index],
                    conversationItemFingerprints[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        requestInput = input.Skip(continuationState.ConversationItemFingerprints.Count).ToArray();
        return requestInput.Count > 0;
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

    private static CodexReasoningOptions? BuildReasoningOptions(
        bool isReasoningModel,
        ReasoningOptions? reasoning)
    {
        if (!isReasoningModel)
        {
            return null;
        }

        var summary = reasoning?.Output == ReasoningOutput.None ? null : "auto";
        return new CodexReasoningOptions(ToOpenAiReasoningEffort(reasoning?.Effort) ?? "medium", summary, null);
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

    private static string? GetModelOption(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } properties
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

    private static IReadOnlyList<string> BuildItemFingerprints(IEnumerable<object> items)
        => items.Select(BuildItemFingerprint).ToArray();

    private static string BuildItemFingerprint(object item)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions))));

    private static string BuildShapeFingerprint(
        string model,
        string? instructions,
        IReadOnlyList<object> tools,
        string? toolChoice,
        bool? parallelToolCalls,
        IReadOnlyList<string>? include,
        string? serviceTier,
        CodexReasoningOptions? reasoning,
        CodexTextOptions? text)
        => BuildItemFingerprint(new CodexContinuationShape(
            model,
            instructions,
            tools,
            toolChoice,
            parallelToolCalls,
            include,
            serviceTier,
            reasoning,
            text));

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

        [JsonPropertyName("parallel_tool_calls")]
        public bool? ParallelToolCalls { get; init; }

        [JsonPropertyName("stream")]
        public required bool Stream { get; init; }

        [JsonPropertyName("store")]
        public required bool Store { get; init; }

        [JsonPropertyName("previous_response_id")]
        public string? PreviousResponseId { get; init; }

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
        [property: JsonPropertyName("effort")] string? Effort,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("mode")] string? Mode);

    private sealed record CodexTextOptions(
        [property: JsonPropertyName("verbosity")] string Verbosity);

    private sealed record CodexContinuationShape(
        string Model,
        string? Instructions,
        IReadOnlyList<object> Tools,
        string? ToolChoice,
        bool? ParallelToolCalls,
        IReadOnlyList<string>? Include,
        string? ServiceTier,
        CodexReasoningOptions? Reasoning,
        CodexTextOptions? Text);
}

internal sealed record CodexResponsesRequest(
    string Model,
    int InputItemCount,
    int ConversationInputItemCount,
    int ToolCount,
    string Body,
    bool UsesDeveloperInstructionInput,
    bool HasTopLevelInstructions,
    string? ServiceTier,
    bool HasPromptCacheKey,
    bool HasIncludeOptions,
    bool HasReasoningOptions,
    bool HasTextOptions,
    string? ToolChoice,
    bool? ParallelToolCalls,
    bool HasPreviousResponseId,
    string ShapeFingerprint,
    IReadOnlyList<string> ConversationItemFingerprints);

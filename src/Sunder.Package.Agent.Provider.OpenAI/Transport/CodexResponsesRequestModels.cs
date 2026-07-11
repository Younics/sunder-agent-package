using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal sealed record CodexNormalizedResponsesRequest(
    string Model,
    IReadOnlyList<object> Input,
    string? Instructions,
    IReadOnlyList<object> Tools,
    object? ToolChoice,
    bool? ParallelToolCalls,
    string? PromptCacheKey,
    IReadOnlyList<string>? Include,
    string? ServiceTier,
    int? MaxOutputTokens,
    CodexReasoningOptions? Reasoning,
    CodexTextOptions? Text,
    bool UsesDeveloperInstructionInput);

internal sealed record CodexInstructionInput(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record CodexUserTextInput(
    [property: JsonPropertyName("content")] IReadOnlyList<object> Content)
{
    [JsonPropertyName("role")]
    public string Role { get; } = "user";
}

internal sealed record CodexAssistantTextInput(
    [property: JsonPropertyName("content")] IReadOnlyList<CodexTextPart> Content)
{
    [JsonPropertyName("role")]
    public string Role { get; } = "assistant";
}

internal sealed record CodexTextPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record CodexImagePart(
    [property: JsonPropertyName("image_url")] string ImageUrl)
{
    [JsonPropertyName("type")]
    public string Type { get; } = "input_image";
}

internal sealed record CodexFilePart(
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("file_data")] string FileData)
{
    [JsonPropertyName("type")]
    public string Type { get; } = "input_file";
}

internal sealed record CodexFunctionCallInput(
    [property: JsonPropertyName("call_id")] string CallId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments)
{
    [JsonPropertyName("type")]
    public string Type { get; } = "function_call";
}

internal sealed record CodexFunctionCallOutputInput(
    [property: JsonPropertyName("call_id")] string CallId,
    [property: JsonPropertyName("output")] string Output)
{
    [JsonPropertyName("type")]
    public string Type { get; } = "function_call_output";
}

internal sealed record CodexFunctionTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters,
    [property: JsonPropertyName("strict")] bool Strict)
{
    [JsonPropertyName("type")]
    public string Type { get; } = "function";
}

internal sealed record CodexRequiredFunctionToolChoice(
    [property: JsonPropertyName("name")] string Name)
{
    [JsonPropertyName("type")]
    public string Type { get; } = "function";
}

internal sealed record CodexReasoningOptions(
    [property: JsonPropertyName("effort")] string? Effort,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("mode")] string? Mode);

internal sealed record CodexTextOptions(
    [property: JsonPropertyName("verbosity")] string Verbosity);

using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed record GeminiResponseTranslation(
    IReadOnlyList<AIContent> Contents,
    IReadOnlyList<string> UnsupportedPartKinds,
    ProviderUsageSnapshot Usage)
{
    public string FirstEventKind => ProviderResponseUpdates.Describe(Contents);
}

internal sealed class GeminiResponseTranslator
{
    public GeminiResponseTranslation Translate(
        GenerateContentResponse response,
        bool allowMultipleToolCalls)
    {
        var contents = new List<AIContent>();
        var unsupportedPartKinds = new List<string>();
        var parts = response.Candidates?.FirstOrDefault()?.Content?.Parts;
        if (parts is null)
        {
            return new GeminiResponseTranslation(contents, unsupportedPartKinds, GetUsage(response));
        }

        var functionCallCount = parts.Count(part => part.FunctionCall is not null);
        if (functionCallCount > 1 && !allowMultipleToolCalls)
        {
            throw GeminiExceptionMapper.MultipleToolCalls();
        }

        foreach (var part in parts)
        {
            AIContent? translatedContent;
            if (part.Thought == true && (part.Text is not null || part.ThoughtSignature is not null))
            {
                translatedContent = new TextReasoningContent(part.Text ?? string.Empty)
                {
                    ProtectedData = part.ThoughtSignature is null
                        ? null
                        : Convert.ToBase64String(part.ThoughtSignature),
                    RawRepresentation = part,
                };
            }
            else if (part.Text is not null)
            {
                translatedContent = new TextContent(part.Text) { RawRepresentation = part };
            }
            else if (part.FunctionCall is { } functionCall)
            {
                if (functionCall.WillContinue == true || string.IsNullOrWhiteSpace(functionCall.Name))
                {
                    throw GeminiExceptionMapper.MalformedToolCall(
                        "Gemini returned an unfinished function call or omitted its function name.");
                }

                translatedContent = new FunctionCallContent(
                    functionCall.Id ?? Guid.NewGuid().ToString("N"),
                    functionCall.Name,
                    ParseArgumentsJson(JsonSerializer.Serialize(
                        functionCall.Args ?? new Dictionary<string, object>())))
                {
                    RawRepresentation = part,
                };
            }
            else if (part.InlineData is { Data: { } data } inlineData)
            {
                translatedContent = new DataContent(data, inlineData.MimeType ?? "application/octet-stream")
                {
                    Name = inlineData.DisplayName,
                    RawRepresentation = part,
                };
            }
            else
            {
                unsupportedPartKinds.Add(GetPartKind(part));
                continue;
            }

            contents.Add(translatedContent);
            if (part.Thought != true && part.ThoughtSignature is { } thoughtSignature)
            {
                contents.Add(new TextReasoningContent(string.Empty)
                {
                    ProtectedData = Convert.ToBase64String(thoughtSignature),
                    RawRepresentation = part,
                });
            }
        }

        return new GeminiResponseTranslation(contents, unsupportedPartKinds, GetUsage(response));
    }

    internal static IDictionary<string, object?> ParseArgumentsJson(string? argumentsJson)
        => ProviderJson.TryParseObjectArguments(argumentsJson, out var arguments)
            ? arguments
            : throw GeminiExceptionMapper.MalformedToolCall(
                "Gemini returned function arguments that were not a valid JSON object.");

    internal static GeminiTerminalStatus GetTerminalStatus(GenerateContentResponse response)
    {
        var candidate = response.Candidates?.FirstOrDefault();
        if (candidate is null)
        {
            return response.PromptFeedback is null
                ? GeminiTerminalStatus.Pending
                : GeminiTerminalStatus.Failed(
                    $"Gemini blocked the prompt: {JsonSerializer.Serialize(response.PromptFeedback)}");
        }

        if (candidate.FinishReason is null
            || candidate.FinishReason == FinishReason.FinishReasonUnspecified)
        {
            return GeminiTerminalStatus.Pending;
        }

        return candidate.FinishReason == FinishReason.Stop
            ? GeminiTerminalStatus.Succeeded
            : GeminiTerminalStatus.Failed(string.IsNullOrWhiteSpace(candidate.FinishMessage)
                ? $"Gemini stopped with '{candidate.FinishReason}'."
                : $"Gemini stopped with '{candidate.FinishReason}': {candidate.FinishMessage}");
    }

    private static string GetPartKind(Part part)
    {
        if (part.FileData is not null) return nameof(part.FileData);
        if (part.FunctionResponse is not null) return nameof(part.FunctionResponse);
        if (part.ExecutableCode is not null) return nameof(part.ExecutableCode);
        if (part.CodeExecutionResult is not null) return nameof(part.CodeExecutionResult);
        if (part.ToolCall is not null) return nameof(part.ToolCall);
        if (part.ToolResponse is not null) return nameof(part.ToolResponse);
        return "UnknownPart";
    }

    private static ProviderUsageSnapshot GetUsage(GenerateContentResponse response)
        => response.UsageMetadata is { } usage
            ? new ProviderUsageSnapshot(
                usage.PromptTokenCount,
                usage.CandidatesTokenCount,
                usage.TotalTokenCount,
                usage.CachedContentTokenCount,
                usage.ThoughtsTokenCount)
            : default;
}

internal readonly record struct GeminiTerminalStatus(bool IsTerminal, bool IsSuccess, string? Detail)
{
    public static GeminiTerminalStatus Pending => new(false, false, null);
    public static GeminiTerminalStatus Succeeded => new(true, true, null);
    public static GeminiTerminalStatus Failed(string detail) => new(true, false, detail);
}

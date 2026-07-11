using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using AIChatTool = Microsoft.Extensions.AI.AITool;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

internal static class CodexResponsesInputNormalizer
{
    public static IReadOnlyList<object> BuildNativeInput(
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

    public static IReadOnlyList<object> BuildFunctionTools(IList<AIChatTool> tools)
        => tools
            .OfType<AIFunctionDeclaration>()
            .Select(tool => new CodexFunctionTool(
                tool.Name,
                tool.Description,
                OpenAiStrictToolSchemaNormalizer.NormalizeFunctionParameters(BuildToolSchemaJson(tool.JsonSchema)),
                Strict: false))
            .ToArray();

    public static IReadOnlyList<object> BuildAssistantOutput(
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

        return output;
    }

    public static bool UsesDeveloperInstructionInput(
        IEnumerable<AIChatMessage> messages,
        bool useDeveloperInstructions)
        => useDeveloperInstructions && messages.Any(message => message.Role == AIChatRole.System && HasText(message));

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
                case DataContent dataContent when userContentParts is not null
                                                  && TryBuildAttachmentPart(dataContent, out var attachmentPart):
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
        if (userContentParts.Count > 0)
        {
            input.Add(new CodexUserTextInput(userContentParts.ToArray()));
            userContentParts.Clear();
        }
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
        input.Add(role == AIChatRole.System
            ? new CodexInstructionInput(useDeveloperInstructions ? "developer" : "system", text)
            : role == AIChatRole.Assistant
                ? new CodexAssistantTextInput([new CodexTextPart("output_text", text)])
                : new CodexUserTextInput([new CodexTextPart("input_text", text)]));
        textBuilder.Clear();
    }

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

    private static bool HasText(AIChatMessage message)
        => !string.IsNullOrWhiteSpace(message.Text)
           || message.Contents.OfType<TextContent>().Any(content => !string.IsNullOrWhiteSpace(content.Text));

    private static string? BuildToolSchemaJson(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Undefined ? null : schema.GetRawText();

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
}

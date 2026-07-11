using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using AIChatTool = Microsoft.Extensions.AI.AITool;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal static class AnthropicMessageTranslator
{
    internal static List<MessageParam> TranslateMessages(IEnumerable<AIChatMessage> chatMessages)
    {
        var messages = new List<MessageParam>();
        foreach (var message in chatMessages)
        {
            AddMessages(messages, message);
        }

        return messages;
    }

    internal static IReadOnlyList<ToolUnion> TranslateTools(IList<AIChatTool> tools)
        => tools
            .OfType<AIFunctionDeclaration>()
            .Select(tool => (ToolUnion)new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = new InputSchema
                {
                    Properties = ParseSchemaProperties(tool.JsonSchema),
                    Required = ParseRequiredProperties(tool.JsonSchema),
                },
            })
            .ToArray();

    private static void AddMessages(ICollection<MessageParam> messages, AIChatMessage message)
    {
        var textBuilder = new StringBuilder();
        var blocks = new List<ContentBlockParam>();
        var translatedContent = false;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoningContent when message.Role == AIChatRole.Assistant
                                                                && !string.IsNullOrWhiteSpace(reasoningContent.ProtectedData):
                    translatedContent = true;
                    FlushTextBlock(blocks, textBuilder);
                    blocks.Add(new ThinkingBlockParam
                    {
                        Thinking = reasoningContent.Text,
                        Signature = reasoningContent.ProtectedData,
                    });
                    break;

                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    translatedContent = true;
                    AppendText(textBuilder, textContent.Text);
                    break;

                case DataContent dataContent when message.Role == AIChatRole.User
                                                  && TryTranslateAttachment(dataContent, out var attachment):
                    translatedContent = true;
                    FlushTextBlock(blocks, textBuilder);
                    blocks.Add(attachment);
                    break;

                case FunctionCallContent functionCall:
                    translatedContent = true;
                    FlushTextBlock(blocks, textBuilder);
                    blocks.Add(TranslateFunctionCall(functionCall));
                    break;

                case FunctionResultContent functionResult:
                    translatedContent = true;
                    FlushTextBlock(blocks, textBuilder);
                    blocks.Add(TranslateFunctionResult(functionResult));
                    break;
            }
        }

        if (!translatedContent && !string.IsNullOrWhiteSpace(message.Text))
        {
            textBuilder.Append(message.Text);
        }

        FlushTextBlock(blocks, textBuilder);
        if (blocks.Count > 0)
        {
            messages.Add(new MessageParam
            {
                Role = message.Role == AIChatRole.Assistant ? Role.Assistant : Role.User,
                Content = blocks,
            });
        }
    }

    private static ToolUseBlockParam TranslateFunctionCall(FunctionCallContent functionCall)
        => new()
        {
            ID = string.IsNullOrWhiteSpace(functionCall.CallId) ? Guid.NewGuid().ToString("N") : functionCall.CallId,
            Name = functionCall.Name,
            Input = ToJsonElementMap(functionCall.Arguments),
        };

    private static ToolResultBlockParam TranslateFunctionResult(FunctionResultContent functionResult)
        => new()
        {
            ToolUseID = functionResult.CallId,
            Content = ProviderToolResult.RenderText(functionResult.Result),
            IsError = functionResult.Exception is not null,
        };

    private static void FlushTextBlock(ICollection<ContentBlockParam> blocks, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        blocks.Add(new TextBlockParam { Text = textBuilder.ToString() });
        textBuilder.Clear();
    }

    private static bool TryTranslateAttachment(DataContent dataContent, out ContentBlockParam block)
    {
        var mediaType = dataContent.MediaType ?? string.Empty;
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            block = new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    MediaType = mediaType,
                    Data = dataContent.Base64Data.ToString(),
                },
            };
            return true;
        }

        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            block = new DocumentBlockParam
            {
                Source = new Base64PdfSource
                {
                    MediaType = JsonSerializer.SerializeToElement("application/pdf"),
                    Data = dataContent.Base64Data.ToString(),
                },
                Title = dataContent.Name,
            };
            return true;
        }

        block = null!;
        return false;
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

    private static Dictionary<string, JsonElement> ParseSchemaProperties(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return properties.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseRequiredProperties(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return required.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, JsonElement> ToJsonElementMap(
        IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }
}

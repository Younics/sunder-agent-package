using System.Text;
using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Provider.Shared;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using GenAIContent = Google.GenAI.Types.Content;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed record GeminiContentTranslation(
    List<GenAIContent> Contents,
    GenAIContent? SystemInstruction);

internal sealed class GeminiContentTranslator
{
    private static readonly byte[] SkipThoughtValidation =
        Encoding.UTF8.GetBytes("skip_thought_signature_validator");

    public GeminiContentTranslation Translate(IEnumerable<AIChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var contents = new List<GenAIContent>();
        var systemParts = new List<Part>();
        var correlation = new GeminiToolCorrelation();

        foreach (var message in messages)
        {
            if (message.Role == AIChatRole.System)
            {
                AddSystemParts(message, systemParts);
                continue;
            }

            var parts = TranslateParts(message, correlation);
            if (parts.Count == 0)
            {
                continue;
            }

            contents.Add(new GenAIContent
            {
                Role = TranslateRole(message.Role),
                Parts = parts,
            });
        }

        return new GeminiContentTranslation(
            contents,
            systemParts.Count == 0 ? null : new GenAIContent { Parts = systemParts });
    }

    private static void AddSystemParts(AIChatMessage message, ICollection<Part> parts)
    {
        if (message.Contents.Count == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            parts.Add(new Part { Text = message.Text });
            return;
        }

        foreach (var content in message.Contents)
        {
            if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
            {
                parts.Add(new Part { Text = text.Text });
                continue;
            }

            if (content is TextContent)
            {
                continue;
            }

            throw GeminiExceptionMapper.UnsupportedContent(content, AIChatRole.System);
        }
    }

    private static List<Part> TranslateParts(AIChatMessage message, GeminiToolCorrelation correlation)
    {
        var parts = new List<Part>(message.Contents.Count);
        if (message.Contents.Count == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            parts.Add(new Part { Text = message.Text });
            return parts;
        }

        for (var index = 0; index < message.Contents.Count; index++)
        {
            var content = message.Contents[index];
            byte[]? associatedThoughtSignature = null;
            if (content is not TextReasoningContent
                && index + 1 < message.Contents.Count
                && message.Contents[index + 1] is TextReasoningContent
                {
                    Text: null or "",
                    ProtectedData: not null,
                } signatureContent)
            {
                associatedThoughtSignature = DecodeThoughtSignature(signatureContent.ProtectedData);
                index++;
            }

            Part part;
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    part = new Part { Text = text.Text };
                    break;

                case TextContent:
                    continue;

                case TextReasoningContent reasoning:
                    part = TranslateReasoning(reasoning);
                    break;

                case DataContent data:
                    part = new Part
                    {
                        InlineData = new Blob
                        {
                            Data = data.Data.ToArray(),
                            MimeType = data.MediaType,
                            DisplayName = data.Name,
                        },
                    };
                    break;

                case UriContent uri:
                    part = new Part
                    {
                        FileData = new FileData
                        {
                            FileUri = uri.Uri.AbsoluteUri,
                            MimeType = uri.MediaType,
                        },
                    };
                    break;

                case FunctionCallContent functionCall when message.Role == AIChatRole.Assistant:
                    correlation.Register(functionCall.CallId, functionCall.Name);
                    part = new Part
                    {
                        FunctionCall = new FunctionCall
                        {
                            Id = functionCall.CallId,
                            Name = functionCall.Name,
                            Args = ToObjectMap(functionCall.Arguments),
                        },
                        ThoughtSignature = associatedThoughtSignature ?? SkipThoughtValidation,
                    };
                    break;

                case FunctionResultContent functionResult when message.Role is var role
                    && (role == AIChatRole.Tool || role == AIChatRole.User):
                    part = new Part
                    {
                        FunctionResponse = new FunctionResponse
                        {
                            Id = functionResult.CallId,
                            Name = correlation.Resolve(functionResult.CallId),
                            Response = BuildFunctionResponsePayload(functionResult),
                        },
                    };
                    break;

                default:
                    throw GeminiExceptionMapper.UnsupportedContent(content, message.Role);
            }

            part.ThoughtSignature ??= associatedThoughtSignature;
            parts.Add(part);
        }

        return parts;
    }

    private static Part TranslateReasoning(TextReasoningContent reasoning)
    {
        return new Part
        {
            Thought = true,
            Text = string.IsNullOrWhiteSpace(reasoning.Text) ? null : reasoning.Text,
            ThoughtSignature = string.IsNullOrWhiteSpace(reasoning.ProtectedData)
                ? null
                : DecodeThoughtSignature(reasoning.ProtectedData),
        };
    }

    private static byte[] DecodeThoughtSignature(string protectedData)
    {
        try
        {
            return Convert.FromBase64String(protectedData);
        }
        catch (FormatException ex)
        {
            throw GeminiExceptionMapper.InvalidThoughtSignature(ex);
        }
    }

    private static string TranslateRole(AIChatRole role)
    {
        if (role == AIChatRole.Assistant)
        {
            return "model";
        }

        if (role == AIChatRole.User || role == AIChatRole.Tool)
        {
            return "user";
        }

        throw GeminiExceptionMapper.UnsupportedRole(role);
    }

    private static Dictionary<string, object> ToObjectMap(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(arguments)) ?? [];
    }

    private static Dictionary<string, object> BuildFunctionResponsePayload(FunctionResultContent result)
        => new()
        {
            [result.Exception is null ? "output" : "error"] = ProviderToolResult.RenderText(result.Result),
        };
}

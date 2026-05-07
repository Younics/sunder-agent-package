using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using AIChatTool = Microsoft.Extensions.AI.AITool;
using AIChatToolMode = Microsoft.Extensions.AI.ChatToolMode;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal sealed class AnthropicChatClient(
    AgentChatClientContext context,
    Func<string?> apiKeyFactory) : IChatClient
{
    private readonly AgentChatClientContext _context = context;
    private readonly Func<string?> _apiKeyFactory = apiKeyFactory;

    public ChatClientMetadata Metadata { get; } = new("Anthropic");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var responseMessage = new AIChatMessage(AIChatRole.Assistant, []);
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                responseMessage.Contents.Add(content);
            }
        }

        return new ChatResponse(responseMessage)
        {
            ModelId = options?.ModelId ?? _context.ModelId,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeyFactory();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AgentChatProviderException(
                "missing-api-key",
                "### Missing Anthropic API key\n\nOpen **Settings -> Packages -> Sunder Agent Provider Anthropic** and enter an API key before sending messages.",
                "missing-api-key");
        }

        var modelId = options?.ModelId ?? _context.ModelId;
        var includeTools = options?.ToolMode != AIChatToolMode.None && options?.Tools is { Count: > 0 };
        var parameters = BuildMessageCreateParams(messages, options, modelId, includeTools);
        await LogAsync(
            AgentLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model.id"] = modelId,
                ["prompt.turn_count"] = parameters.Messages.Count,
                ["tool.available_count"] = includeTools ? parameters.Tools?.Count ?? 0 : 0,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);
        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.start",
            "Provider stream started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["provider.id"] = _context.ProviderId,
                ["model.id"] = modelId,
                ["tool.count"] = includeTools ? parameters.Tools?.Count ?? 0 : 0,
                ["message.count"] = parameters.Messages.Count,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);

        var client = new AnthropicClient { ApiKey = apiKey };
        if (includeTools)
        {
            await foreach (var update in GetToolAwareResponseAsync(client, parameters, modelId, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        await foreach (var update in GetTextStreamingResponseAsync(client, parameters, modelId, cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(System.Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? Metadata
                : null;

    public void Dispose()
    {
    }

    private async IAsyncEnumerable<ChatResponseUpdate> GetToolAwareResponseAsync(
        AnthropicClient client,
        MessageCreateParams parameters,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Message response;
        try
        {
            response = await client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
            throw CreateProviderException(ex);
        }

        var responseId = Guid.NewGuid().ToString("N");
        var messageId = responseId;
        var toolCalls = response.Content
            .Where(block => block.TryPickToolUse(out _))
            .Select(block =>
            {
                block.TryPickToolUse(out var toolUse);
                return toolUse!;
            })
            .ToArray();

        if (toolCalls.Length > 1)
        {
            throw new AgentChatProviderException(
                "anthropic-multiple-tool-calls",
                "### Anthropic requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
                "anthropic-multiple-tool-calls");
        }

        if (toolCalls.Length == 1)
        {
            var toolCall = toolCalls[0];
            await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "ToolCallRequested", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            yield return new ChatResponseUpdate(AIChatRole.Assistant,
                [new FunctionCallContent(
                    string.IsNullOrWhiteSpace(toolCall.ID) ? Guid.NewGuid().ToString("N") : toolCall.ID,
                    string.IsNullOrWhiteSpace(toolCall.Name) ? "unknown_tool" : toolCall.Name,
                    ParseObjectArguments(JsonSerializer.Serialize(toolCall.Input)))])
            {
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = modelId,
            };
            yield break;
        }

        var text = string.Concat(response.Content
            .Where(block => block.TryPickText(out _))
            .Select(block =>
            {
                block.TryPickText(out var textBlock);
                return textBlock?.Text;
            })
            .Where(textPart => !string.IsNullOrWhiteSpace(textPart)));

        if (!string.IsNullOrWhiteSpace(text))
        {
            await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "TextDelta", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            yield return new ChatResponseUpdate(AIChatRole.Assistant, text)
            {
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = modelId,
            };
        }

        await LogAsync(AgentLogLevel.Debug, "provider.stream.completed", string.IsNullOrWhiteSpace(text) ? "Provider stream ended without events." : null, stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> GetTextStreamingResponseAsync(
        AnthropicClient client,
        MessageCreateParams parameters,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<RawMessageStreamEvent> stream;
        try
        {
            stream = client.Messages.CreateStreaming(parameters, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw CreateProviderException(ex);
        }

        var responseId = Guid.NewGuid().ToString("N");
        var messageId = responseId;
        var stopwatch = Stopwatch.StartNew();
        var firstEventRecorded = false;
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            RawMessageStreamEvent rawEvent;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                rawEvent = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                await LogAsync(AgentLogLevel.Warning, "provider.stream.canceled", "Provider stream was canceled.", stopwatch.ElapsedMilliseconds, cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
                throw CreateProviderException(ex);
            }

            if (!rawEvent.TryPickContentBlockDelta(out var delta) || !delta.Delta.TryPickText(out var text))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            if (!firstEventRecorded)
            {
                firstEventRecorded = true;
                await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "TextDelta", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            }

            yield return new ChatResponseUpdate(AIChatRole.Assistant, text.Text)
            {
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = modelId,
            };
        }

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.completed",
            firstEventRecorded ? null : "Provider stream ended without events.",
            stopwatch.ElapsedMilliseconds,
            cancellationToken: cancellationToken);
    }

    private ValueTask LogAsync(
        AgentLogLevel level,
        string eventName,
        string? message = null,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => _context.LogProviderEventAsync(level, eventName, message ?? eventName, elapsedMilliseconds, attributes, exception, cancellationToken);

    private static MessageCreateParams BuildMessageCreateParams(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options,
        string modelId,
        bool includeTools)
    {
        var parameters = new MessageCreateParams
        {
            MaxTokens = options?.MaxOutputTokens ?? 8192,
            Messages = BuildMessages(messages),
            Model = NormalizeModelId(modelId),
        };

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            parameters = parameters with { System = options.Instructions };
        }

        if (includeTools && options?.Tools is { Count: > 0 })
        {
            parameters = parameters with { Tools = BuildTools(options.Tools) };
        }

        return parameters;
    }

    private static List<MessageParam> BuildMessages(IEnumerable<AIChatMessage> chatMessages)
    {
        var messages = new List<MessageParam>();
        foreach (var message in chatMessages)
        {
            AddMessages(messages, message);
        }

        return messages;
    }

    private static void AddMessages(ICollection<MessageParam> messages, AIChatMessage message)
    {
        var textBuilder = new StringBuilder();
        var userBlocks = message.Role == AIChatRole.User ? new List<ContentBlockParam>() : null;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    AppendText(textBuilder, textContent.Text);
                    break;

                case DataContent dataContent when userBlocks is not null && TryBuildAttachmentBlock(dataContent, out var attachmentBlock):
                    FlushTextBlock(userBlocks, textBuilder);
                    userBlocks.Add(attachmentBlock);
                    break;

                case FunctionCallContent functionCall:
                    FlushMessage(messages, message.Role, textBuilder, userBlocks);
                    messages.Add(new MessageParam
                    {
                        Role = Role.Assistant,
                        Content = new List<ContentBlockParam>
                        {
                            new ToolUseBlockParam
                            {
                                ID = string.IsNullOrWhiteSpace(functionCall.CallId) ? Guid.NewGuid().ToString("N") : functionCall.CallId,
                                Name = functionCall.Name,
                                Input = ToJsonElementMap(functionCall.Arguments),
                            },
                        },
                    });
                    break;

                case FunctionResultContent functionResult:
                    FlushMessage(messages, message.Role, textBuilder, userBlocks);
                    messages.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = new List<ContentBlockParam>
                        {
                            new ToolResultBlockParam
                            {
                                ToolUseID = functionResult.CallId,
                                Content = RenderFunctionResult(functionResult.Result),
                                IsError = functionResult.Exception is not null,
                            },
                        },
                    });
                    break;
            }
        }

        if (textBuilder.Length == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            textBuilder.Append(message.Text);
        }

        FlushMessage(messages, message.Role, textBuilder, userBlocks);
    }

    private static void FlushMessage(
        ICollection<MessageParam> messages,
        AIChatRole role,
        StringBuilder textBuilder,
        List<ContentBlockParam>? userBlocks)
    {
        if (userBlocks is null)
        {
            FlushTextMessage(messages, role, textBuilder);
            return;
        }

        FlushTextBlock(userBlocks, textBuilder);
        if (userBlocks.Count == 0)
        {
            return;
        }

        messages.Add(new MessageParam
        {
            Role = Role.User,
            Content = new List<ContentBlockParam>(userBlocks),
        });
        userBlocks.Clear();
    }

    private static void FlushTextBlock(ICollection<ContentBlockParam> blocks, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        blocks.Add(new TextBlockParam
        {
            Text = textBuilder.ToString(),
        });
        textBuilder.Clear();
    }

    private static bool TryBuildAttachmentBlock(DataContent dataContent, out ContentBlockParam block)
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

    private static void FlushTextMessage(ICollection<MessageParam> messages, AIChatRole role, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        messages.Add(new MessageParam
        {
            Role = role == AIChatRole.Assistant ? Role.Assistant : Role.User,
            Content = textBuilder.ToString(),
        });
        textBuilder.Clear();
    }

    private static IReadOnlyList<ToolUnion> BuildTools(IList<AIChatTool> tools)
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

    private static IReadOnlyDictionary<string, JsonElement> ToJsonElementMap(IDictionary<string, object?>? arguments)
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

    private static IDictionary<string, object?> ParseObjectArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = document.RootElement.Clone(),
                };
            }

            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = argumentsJson,
            };
        }
    }

    private static string RenderFunctionResult(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };

    private static AgentChatProviderException CreateProviderException(Exception exception)
        => new(
            exception.Message,
            $"### Anthropic request failed\n\n{exception.Message}",
            "anthropic-http-error",
            exception);

    private static string NormalizeModelId(string modelId)
    {
        const string prefix = "anthropic/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }
}

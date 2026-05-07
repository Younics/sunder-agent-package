using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using AIChatTool = Microsoft.Extensions.AI.AITool;
using AIChatToolMode = Microsoft.Extensions.AI.ChatToolMode;
using GenAIContent = Google.GenAI.Types.Content;
using GenAITool = Google.GenAI.Types.Tool;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiChatClient(
    AgentChatClientContext context,
    Func<string?> apiKeyFactory) : IChatClient
{
    private readonly AgentChatClientContext _context = context;
    private readonly Func<string?> _apiKeyFactory = apiKeyFactory;

    public ChatClientMetadata Metadata { get; } = new("Google Gemini");

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
                "### Missing Gemini API key\n\nOpen **Settings -> Packages -> Sunder Agent Provider Gemini** and enter an API key before sending messages.",
                "missing-api-key");
        }

        var modelId = options?.ModelId ?? _context.ModelId;
        var includeTools = options?.ToolMode != AIChatToolMode.None && options?.Tools is { Count: > 0 };
        var contents = BuildContents(messages);
        var config = BuildConfig(options, includeTools);
        await LogAsync(
            AgentLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model.id"] = modelId,
                ["prompt.turn_count"] = contents.Count,
                ["tool.available_count"] = includeTools ? options?.Tools?.Count ?? 0 : 0,
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
                ["tool.count"] = includeTools ? options?.Tools?.Count ?? 0 : 0,
                ["message.count"] = contents.Count,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);

        Client client;
        try
        {
            client = new Client(apiKey: apiKey);
        }
        catch (Exception ex)
        {
            throw new AgentChatProviderException(
                ex.Message,
                $"### Gemini client setup failed\n\n{ex.Message}",
                "gemini-client-init",
                ex);
        }

        if (includeTools)
        {
            await foreach (var update in GetToolAwareResponseAsync(client, contents, config, modelId, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        await foreach (var update in GetTextStreamingResponseAsync(client, contents, config, modelId, cancellationToken))
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
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        GenerateContentResponse response;
        try
        {
            response = await client.Models.GenerateContentAsync(
                model: NormalizeModelId(modelId),
                contents: contents,
                config: config,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, stopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
            throw CreateProviderException(ex);
        }

        var responseId = Guid.NewGuid().ToString("N");
        var messageId = responseId;
        if (response.FunctionCalls is { Count: > 1 })
        {
            throw new AgentChatProviderException(
                "gemini-multiple-tool-calls",
                "### Gemini requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
                "gemini-multiple-tool-calls");
        }

        if (response.FunctionCalls is { Count: 1 })
        {
            var functionCall = response.FunctionCalls[0];
            await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "ToolCallRequested", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            yield return new ChatResponseUpdate(AIChatRole.Assistant,
                [new FunctionCallContent(
                    functionCall.Id ?? Guid.NewGuid().ToString("N"),
                    functionCall.Name ?? "unknown_tool",
                    ParseObjectArguments(JsonSerializer.Serialize(functionCall.Args ?? new Dictionary<string, object>())))])
            {
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = modelId,
            };
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "TextDelta", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            yield return new ChatResponseUpdate(AIChatRole.Assistant, response.Text)
            {
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = modelId,
            };
        }

        await LogAsync(AgentLogLevel.Debug, "provider.stream.completed", string.IsNullOrWhiteSpace(response.Text) ? "Provider stream ended without events." : null, stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> GetTextStreamingResponseAsync(
        Client client,
        List<GenAIContent> contents,
        GenerateContentConfig config,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<GenerateContentResponse> stream;
        try
        {
            stream = client.Models.GenerateContentStreamAsync(
                model: NormalizeModelId(modelId),
                contents: contents,
                config: config,
                cancellationToken: cancellationToken);
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
            GenerateContentResponse chunk;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                chunk = enumerator.Current;
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

            var delta = ExtractText(chunk);
            if (string.IsNullOrWhiteSpace(delta))
            {
                continue;
            }

            if (!firstEventRecorded)
            {
                firstEventRecorded = true;
                await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", "TextDelta", stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);
            }

            yield return new ChatResponseUpdate(AIChatRole.Assistant, delta)
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

    private static List<GenAIContent> BuildContents(IEnumerable<AIChatMessage> messages)
    {
        var contents = new List<GenAIContent>();
        foreach (var message in messages)
        {
            AddContents(contents, message);
        }

        return contents;
    }

    private static void AddContents(ICollection<GenAIContent> contents, AIChatMessage message)
    {
        var textBuilder = new StringBuilder();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    AppendText(textBuilder, textContent.Text);
                    break;

                case DataContent dataContent when dataContent.Data is { } data:
                    FlushTextContent(contents, message.Role, textBuilder);
                    contents.Add(new GenAIContent
                    {
                        Role = message.Role == AIChatRole.Assistant ? "model" : "user",
                        Parts = [new Part
                        {
                            InlineData = new Blob
                            {
                                Data = data.ToArray(),
                                MimeType = dataContent.MediaType,
                                DisplayName = dataContent.Name,
                            }
                        }],
                    });
                    break;

                case FunctionCallContent functionCall:
                    FlushTextContent(contents, message.Role, textBuilder);
                    contents.Add(new GenAIContent
                    {
                        Role = "model",
                        Parts = [new Part
                        {
                            FunctionCall = new FunctionCall
                            {
                                Id = functionCall.CallId,
                                Name = functionCall.Name,
                                Args = ToObjectMap(functionCall.Arguments),
                            }
                        }],
                    });
                    break;

                case FunctionResultContent functionResult:
                    FlushTextContent(contents, message.Role, textBuilder);
                    contents.Add(new GenAIContent
                    {
                        Role = "user",
                        Parts = [new Part
                        {
                            FunctionResponse = new FunctionResponse
                            {
                                Id = functionResult.CallId,
                                Name = null,
                                Response = BuildFunctionResponsePayload(functionResult),
                            }
                        }],
                    });
                    break;
            }
        }

        if (textBuilder.Length == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            textBuilder.Append(message.Text);
        }

        FlushTextContent(contents, message.Role, textBuilder);
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

    private static void FlushTextContent(ICollection<GenAIContent> contents, AIChatRole role, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        contents.Add(new GenAIContent
        {
            Role = role == AIChatRole.Assistant ? "model" : "user",
            Parts = [new Part { Text = textBuilder.ToString() }],
        });
        textBuilder.Clear();
    }

    private static GenerateContentConfig BuildConfig(ChatOptions? options, bool includeTools)
    {
        var config = new GenerateContentConfig();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            config.SystemInstruction = new GenAIContent
            {
                Parts = [new Part { Text = options.Instructions }]
            };
        }

        if (includeTools && options?.Tools is { Count: > 0 })
        {
            config.Tools =
            [
                new GenAITool
                {
                    FunctionDeclarations = options.Tools
                        .OfType<AIFunctionDeclaration>()
                        .Select(tool => new FunctionDeclaration
                        {
                            Name = tool.Name,
                            Description = tool.Description,
                            ParametersJsonSchema = ParseJsonElement(tool.JsonSchema),
                        })
                        .ToList()
                }
            ];
            config.ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingConfigMode.Auto,
                }
            };
        }

        return config;
    }

    private static JsonElement ParseJsonElement(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Undefined)
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false,
            });
        }

        return schema.Clone();
    }

    private static Dictionary<string, object> ToObjectMap(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(arguments)) ?? [];
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

    private static Dictionary<string, object> BuildFunctionResponsePayload(FunctionResultContent result)
    {
        var output = RenderFunctionResult(result.Result);
        return new Dictionary<string, object>
        {
            [result.Exception is null ? "output" : "error"] = output,
        };
    }

    private static string RenderFunctionResult(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };

    private static string? ExtractText(GenerateContentResponse response)
    {
        var parts = response.Candidates?
            .FirstOrDefault()?
            .Content?
            .Parts;

        if (parts is null)
        {
            return null;
        }

        return string.Concat(parts.Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static AgentChatProviderException CreateProviderException(Exception exception)
        => new(
            exception.Message,
            $"### Gemini request failed\n\n{exception.Message}",
            "gemini-http-error",
            exception);

    private static string NormalizeModelId(string modelId)
    {
        const string prefix = "gemini/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }
}

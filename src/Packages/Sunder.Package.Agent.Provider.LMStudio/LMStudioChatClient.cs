using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using AIChatToolMode = Microsoft.Extensions.AI.ChatToolMode;
using AIChatTool = Microsoft.Extensions.AI.AITool;
using OpenAIChatFinishReason = OpenAI.Chat.ChatFinishReason;
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed class LMStudioChatClient(
    AgentChatClientContext context,
    Func<string?> baseUrlFactory,
    Func<string?> apiKeyFactory) : IChatClient
{
    private readonly AgentChatClientContext _context = context;
    private readonly Func<string?> _baseUrlFactory = baseUrlFactory;
    private readonly Func<string?> _apiKeyFactory = apiKeyFactory;

    public ChatClientMetadata Metadata { get; } = new("LM Studio");

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
        var baseUrl = _baseUrlFactory();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new AgentChatProviderException(
                "lmstudio-base-url-missing",
                "### LM Studio base URL missing\n\nOpen **Settings -> Packages -> Sunder Agent Provider LM Studio** and enter a base URL before sending messages.",
                "lmstudio-base-url-missing");
        }

        var modelId = options?.ModelId ?? _context.ModelId;
        var sdkMessages = BuildChatMessages(messages, options?.Instructions);
        var sdkOptions = BuildChatCompletionOptions(options);
        var responseId = Guid.NewGuid().ToString("N");
        var messageId = responseId;

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model.id"] = modelId,
                ["prompt.turn_count"] = sdkMessages.Count,
                ["tool.available_count"] = sdkOptions.Tools.Count,
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
                ["tool.count"] = sdkOptions.Tools.Count,
                ["message.count"] = sdkMessages.Count,
                ["system_prompt.length"] = options?.Instructions?.Length ?? 0,
            },
            cancellationToken: cancellationToken);

        var client = CreateChatClient(baseUrl, modelId);
        IAsyncEnumerable<StreamingChatCompletionUpdate> stream;
        try
        {
            stream = client.CompleteChatStreamingAsync(sdkMessages, sdkOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw CreateProviderException(ex);
        }

        var streamStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var contentBuilder = new StringBuilder();
        var toolCallAccumulator = new StreamingToolCallAccumulator();
        var firstEventRecorded = false;

        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            StreamingChatCompletionUpdate? update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                update = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                await LogAsync(AgentLogLevel.Warning, "provider.stream.canceled", "Provider stream was canceled.", streamStopwatch.ElapsedMilliseconds, cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await LogAsync(AgentLogLevel.Error, "provider.stream.failed", ex.Message, streamStopwatch.ElapsedMilliseconds, exception: ex, cancellationToken: CancellationToken.None);
                throw CreateProviderException(ex);
            }

            foreach (var contentPart in update.ContentUpdate)
            {
                var delta = contentPart.Text;
                if (string.IsNullOrEmpty(delta))
                {
                    continue;
                }

                contentBuilder.Append(delta);
                firstEventRecorded = await RecordFirstEventAsync(firstEventRecorded, "TextDelta", streamStopwatch.ElapsedMilliseconds, cancellationToken);
                yield return new ChatResponseUpdate(AIChatRole.Assistant, delta)
                {
                    ResponseId = responseId,
                    MessageId = messageId,
                    ModelId = modelId,
                };
            }

            foreach (var toolCallUpdate in update.ToolCallUpdates)
            {
                toolCallAccumulator.Apply(toolCallUpdate, out var tooManyToolCalls);
                if (tooManyToolCalls)
                {
                    throw new AgentChatProviderException(
                        "lmstudio-multiple-tool-calls",
                        "### LM Studio requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
                        "lmstudio-multiple-tool-calls");
                }
            }

            if (update.FinishReason == OpenAIChatFinishReason.ToolCalls && toolCallAccumulator.TryBuild(out var completedToolCall))
            {
                firstEventRecorded = await RecordFirstEventAsync(firstEventRecorded, "ToolCallRequested", streamStopwatch.ElapsedMilliseconds, cancellationToken);
                yield return CreateToolCallUpdate(completedToolCall, responseId, messageId, modelId);
                yield break;
            }
        }

        if (toolCallAccumulator.TryBuild(out var finalToolCall))
        {
            firstEventRecorded = await RecordFirstEventAsync(firstEventRecorded, "ToolCallRequested", streamStopwatch.ElapsedMilliseconds, cancellationToken);
            yield return CreateToolCallUpdate(finalToolCall, responseId, messageId, modelId);
            yield break;
        }

        await LogAsync(
            AgentLogLevel.Debug,
            "provider.stream.completed",
            firstEventRecorded ? null : "Provider stream ended without events.",
            streamStopwatch.ElapsedMilliseconds,
            cancellationToken: cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? Metadata
                : null;

    public void Dispose()
    {
    }

    private ChatClient CreateChatClient(string baseUrl, string modelId)
    {
        var apiKey = _apiKeyFactory();
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "lm-studio" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        return new OpenAIClient(credential, options).GetChatClient(NormalizeModelId(modelId));
    }

    private async ValueTask<bool> RecordFirstEventAsync(
        bool firstEventRecorded,
        string eventType,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (firstEventRecorded)
        {
            return true;
        }

        await LogAsync(AgentLogLevel.Debug, "provider.stream.first_event", eventType, elapsedMilliseconds, cancellationToken: cancellationToken);
        return true;
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

    private static IReadOnlyList<OpenAIChatMessage> BuildChatMessages(
        IEnumerable<AIChatMessage> messages,
        string? instructions)
    {
        var sdkMessages = new List<OpenAIChatMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sdkMessages.Add(OpenAIChatMessage.CreateSystemMessage(instructions));
        }

        foreach (var message in messages)
        {
            AddChatMessages(sdkMessages, message);
        }

        return sdkMessages;
    }

    private static void AddChatMessages(ICollection<OpenAIChatMessage> messages, AIChatMessage message)
    {
        var textBuilder = new StringBuilder();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    AppendText(textBuilder, textContent.Text);
                    break;

                case FunctionCallContent functionCall:
                    FlushTextMessage(messages, message.Role, textBuilder);
                    messages.Add(OpenAIChatMessage.CreateAssistantMessage([
                        ChatToolCall.CreateFunctionToolCall(
                            string.IsNullOrWhiteSpace(functionCall.CallId) ? Guid.NewGuid().ToString("N") : functionCall.CallId,
                            functionCall.Name,
                            BinaryData.FromString(SerializeArguments(functionCall.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal))))
                    ]));
                    break;

                case FunctionResultContent functionResult:
                    FlushTextMessage(messages, message.Role, textBuilder);
                    messages.Add(OpenAIChatMessage.CreateToolMessage(functionResult.CallId, RenderFunctionResult(functionResult.Result)));
                    break;
            }
        }

        if (textBuilder.Length == 0 && !string.IsNullOrWhiteSpace(message.Text))
        {
            textBuilder.Append(message.Text);
        }

        FlushTextMessage(messages, message.Role, textBuilder);
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

    private static void FlushTextMessage(ICollection<OpenAIChatMessage> messages, AIChatRole role, StringBuilder textBuilder)
    {
        if (textBuilder.Length == 0)
        {
            return;
        }

        var text = textBuilder.ToString();
        messages.Add(role == AIChatRole.System
            ? OpenAIChatMessage.CreateSystemMessage(text)
            : role == AIChatRole.Assistant
                ? OpenAIChatMessage.CreateAssistantMessage(text)
                : OpenAIChatMessage.CreateUserMessage(text));
        textBuilder.Clear();
    }

    private static ChatCompletionOptions BuildChatCompletionOptions(ChatOptions? options)
    {
        var sdkOptions = new ChatCompletionOptions
        {
            AllowParallelToolCalls = false,
        };

        if (options?.MaxOutputTokens is { } maxOutputTokens)
        {
            sdkOptions.MaxOutputTokenCount = maxOutputTokens;
        }

        if (options?.ToolMode == AIChatToolMode.None)
        {
            return sdkOptions;
        }

        foreach (var tool in BuildChatTools(options?.Tools ?? []))
        {
            sdkOptions.Tools.Add(tool);
        }

        return sdkOptions;
    }

    private static IReadOnlyList<ChatTool> BuildChatTools(IList<AIChatTool> tools)
        => tools
            .OfType<AIFunctionDeclaration>()
            .Select(tool => ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(BuildToolSchemaJson(tool.JsonSchema))))
            .ToArray();

    private static string BuildToolSchemaJson(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false,
            })
            : schema.GetRawText();

    private static ChatResponseUpdate CreateToolCallUpdate(AgentToolCallRequest toolCall, string responseId, string messageId, string modelId)
        => new(AIChatRole.Assistant, [new FunctionCallContent(toolCall.CallId, toolCall.ToolId, ParseArguments(toolCall.ArgumentsJson))])
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
        };

    private static AgentChatProviderException CreateProviderException(Exception exception)
        => new(
            exception.Message,
            $"### LM Studio request failed\n\n{exception.Message}",
            "lmstudio-sdk-error",
            exception);

    private static string NormalizeModelId(string modelId)
    {
        const string prefix = "lmstudio/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }

    private static IDictionary<string, object?> ParseArguments(string argumentsJson)
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

    private static string SerializeArguments(IDictionary<string, object?> arguments)
        => arguments.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(arguments);

    private static string RenderFunctionResult(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };

    private sealed class StreamingToolCallAccumulator
    {
        private readonly StringBuilder _argumentsBuilder = new();

        private string? _callId;

        private string? _toolId;

        public void Apply(StreamingChatToolCallUpdate update, out bool tooManyToolCalls)
        {
            tooManyToolCalls = false;

            if (!string.IsNullOrWhiteSpace(update.ToolCallId))
            {
                if (!string.IsNullOrWhiteSpace(_callId) && !string.Equals(_callId, update.ToolCallId, StringComparison.Ordinal))
                {
                    tooManyToolCalls = true;
                    return;
                }

                _callId = update.ToolCallId;
            }

            if (!string.IsNullOrWhiteSpace(update.FunctionName))
            {
                if (!string.IsNullOrWhiteSpace(_toolId) && !string.Equals(_toolId, update.FunctionName, StringComparison.Ordinal))
                {
                    tooManyToolCalls = true;
                    return;
                }

                _toolId = update.FunctionName;
            }

            if (update.FunctionArgumentsUpdate is not null)
            {
                _argumentsBuilder.Append(update.FunctionArgumentsUpdate.ToString());
            }
        }

        public bool TryBuild(out AgentToolCallRequest toolCall)
        {
            if (string.IsNullOrWhiteSpace(_toolId))
            {
                toolCall = null!;
                return false;
            }

            toolCall = new AgentToolCallRequest(
                string.IsNullOrWhiteSpace(_callId) ? Guid.NewGuid().ToString("N") : _callId,
                _toolId,
                _argumentsBuilder.Length == 0 ? "{}" : _argumentsBuilder.ToString());
            return true;
        }
    }
}

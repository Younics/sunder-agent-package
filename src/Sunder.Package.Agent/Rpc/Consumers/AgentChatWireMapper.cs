using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

#pragma warning disable MEAI001

internal static class AgentChatWireMapper
{
    internal const int MaxContentBytes = 32 * 1024 * 1024;

    public static async ValueTask<IReadOnlyList<AgentChatMessage>> FromChatMessagesAsync(
        IEnumerable<ChatMessage> messages,
        Func<DataContent, CancellationToken, ValueTask<SunderRpcContentReference>> registerContentAsync,
        CancellationToken cancellationToken)
    {
        var mappedMessages = new List<AgentChatMessage>();
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mappedContents = new List<AgentChatContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is DataContent data && !data.Data.IsEmpty)
                {
                    var reference = await registerContentAsync(data, cancellationToken).ConfigureAwait(false);
                    mappedContents.Add(FromContentReference(data, reference));
                }
                else
                {
                    mappedContents.Add(FromContent(content));
                }
            }
            mappedMessages.Add(FromChatMessage(message, mappedContents));
        }
        return mappedMessages;
    }

    public static async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint,
        DataContent content,
        CancellationToken cancellationToken)
    {
        if (content.Data.Length > MaxContentBytes)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.content.length-limit",
                "Chat binary content exceeds the transfer limit."));
        }

        await using var source = new MemoryStream(content.Data.ToArray(), writable: false);
        return await scope.RegisterContentAsync(
            endpoint,
            source,
            new SunderRpcContentRegistrationOptions(
                content.MediaType,
                string.IsNullOrWhiteSpace(content.Name) ? "content.bin" : content.Name,
                content.Data.Length,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<ChatMessage>> ToChatMessagesAsync(
        IReadOnlyList<AgentChatMessage> messages,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
    {
        var contentByReference = new Dictionary<SunderRpcContentReference, ReadOnlyMemory<byte>>();
        foreach (var reference in messages
                     .SelectMany(static message => message.Contents)
                     .Select(static content => content.ContentReference)
                     .Where(static reference => reference is not null)
                     .Cast<SunderRpcContentReference>()
                     .Distinct())
        {
            if (reference.Length is < 0 or > MaxContentBytes)
            {
                throw new InvalidDataException("Chat binary content exceeds the transfer limit.");
            }

            await using var source = await context.OpenContentAsync(reference, cancellationToken).ConfigureAwait(false);
            var bytes = new byte[checked((int)reference.Length)];
            await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            contentByReference.Add(reference, bytes);
        }

        return messages
            .Select(message => ToChatMessage(message, reference => contentByReference[reference]))
            .ToArray();
    }

    public static AgentChatMessage FromChatMessage(ChatMessage message)
        => FromChatMessage(message, message.Contents.Select(FromContent).ToArray());

    public static AgentChatMessage FromChatMessage(
        ChatMessage message,
        IReadOnlyList<AgentChatContent> contents)
        => new(
            message.Role.Value,
            message.MessageId,
            message.AuthorName,
            contents);

    public static ChatMessage ToChatMessage(
        AgentChatMessage message,
        Func<SunderRpcContentReference, ReadOnlyMemory<byte>>? resolveContent = null)
        => new(new ChatRole(message.Role), message.Contents.Select(content => ToContent(content, resolveContent)).ToList())
        {
            MessageId = message.MessageId,
            AuthorName = message.AuthorName,
        };

    public static AgentChatOptions FromChatOptions(ChatOptions? options)
    {
        options ??= new ChatOptions();
        return new AgentChatOptions(
            options.Instructions,
            options.ConversationId,
            options.ModelId,
            options.Temperature,
            options.MaxOutputTokens,
            options.TopP,
            options.TopK,
            options.FrequencyPenalty,
            options.PresencePenalty,
            options.Seed,
            options.AllowMultipleToolCalls,
            options.Reasoning?.Effort.ToString() is { } effort
                ? JsonNamingPolicy.KebabCaseLower.ConvertName(effort)
                : null,
            options.Tools?.OfType<AIFunctionDeclaration>().Select(static tool => new AgentChatTool(
                tool.Name,
                tool.Description,
                tool.JsonSchema.Clone())).ToArray() ?? [],
            options.AdditionalProperties?.ToDictionary(
                static pair => pair.Key,
                static pair => JsonSerializer.SerializeToElement(pair.Value),
                StringComparer.Ordinal),
            options.ContinuationToken is null
                ? null
                : options.ContinuationToken.ToBytes().ToArray());
    }

    public static ChatOptions ToChatOptions(AgentChatOptions options)
    {
        var mapped = new ChatOptions
        {
            Instructions = options.Instructions,
            ConversationId = options.ConversationId,
            ModelId = options.ModelId,
            Temperature = options.Temperature,
            MaxOutputTokens = checked((int?)options.MaxOutputTokens),
            TopP = options.TopP,
            TopK = checked((int?)options.TopK),
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            Tools = options.Tools.Select(static tool => (AITool)AIFunctionFactory.CreateDeclaration(
                tool.Name,
                tool.Description,
                tool.JsonSchema)).ToList(),
        };
        if (Enum.TryParse<ReasoningEffort>(options.ReasoningEffort?.Replace("-", string.Empty), ignoreCase: true, out var effort))
        {
            mapped.Reasoning = new ReasoningOptions { Effort = effort, Output = effort == ReasoningEffort.None ? ReasoningOutput.None : ReasoningOutput.Summary };
        }
        if (options.AdditionalProperties is { Count: > 0 })
        {
            mapped.AdditionalProperties = new AdditionalPropertiesDictionary();
            foreach (var pair in options.AdditionalProperties)
            {
                mapped.AdditionalProperties[pair.Key] = DeserializeAdditionalProperty(pair.Value);
            }
        }
        if (options.ContinuationHandle is { Length: > 0 })
        {
            mapped.ContinuationToken = ResponseContinuationToken.FromBytes(options.ContinuationHandle);
        }
        return mapped;
    }

    public static AgentChatUpdateEvent FromUpdate(ChatResponseUpdate update)
        => new(
            update.Role?.Value,
            update.ResponseId,
            update.MessageId,
            update.ConversationId,
            update.ModelId,
            update.FinishReason?.Value,
            update.CreatedAt is { } createdAt ? ToWireTimestamp(createdAt) : null,
            update.Contents.Select(FromContent).ToArray(),
            update.ContinuationToken?.ToBytes().ToArray());

    public static ChatResponseUpdate ToUpdate(AgentChatUpdateEvent item)
        => new(
            item.Role is null ? null : new ChatRole(item.Role),
            item.Contents.Select(static content => ToContent(content, resolveContent: null)).ToList())
        {
            ResponseId = item.ResponseId,
            MessageId = item.MessageId,
            ConversationId = item.ConversationId,
            ModelId = item.ModelId,
            FinishReason = item.FinishReason is null ? null : new ChatFinishReason(item.FinishReason),
            CreatedAt = item.CreatedAt is { } createdAt
                ? FromWireTimestamp(createdAt)
                : null,
            ContinuationToken = item.ContinuationHandle is null
                ? null
                : ResponseContinuationToken.FromBytes(item.ContinuationHandle),
        };

    public static AgentChatContent FromContent(AIContent content)
        => content switch
        {
            TextReasoningContent reasoning => new("reasoning", Text: reasoning.Text, ProtectedData: reasoning.ProtectedData),
            TextContent text => new("text", Text: text.Text),
            FunctionCallContent call => new("tool-call", CallId: call.CallId, Name: call.Name, Json: new(JsonSerializer.SerializeToElement(call.Arguments)), InformationalOnly: call.InformationalOnly),
            FunctionResultContent result => new("tool-result", CallId: result.CallId, Json: new(JsonSerializer.SerializeToElement(result.Result))),
            UsageContent usage => new("usage", Usage: new AgentChatUsage(
                usage.Details.InputTokenCount,
                usage.Details.OutputTokenCount,
                usage.Details.TotalTokenCount,
                usage.Details.CachedInputTokenCount,
                usage.Details.ReasoningTokenCount)),
            ErrorContent error => new("error", ErrorCode: error.ErrorCode, ErrorMessage: error.Message, Json: new(JsonSerializer.SerializeToElement(error.Details))),
            DataContent data => new("content-reference", Name: data.Name, MediaType: data.MediaType, ContentUri: data.Uri is null ? null : new SunderRpcUri(data.Uri)),
            _ => new("error", ErrorCode: "unsupported-content", ErrorMessage: $"Unsupported AI content type '{content.GetType().Name}'."),
        };

    public static AgentChatContent FromContentReference(
        DataContent content,
        SunderRpcContentReference reference)
        => new(
            "content-reference",
            Name: content.Name,
            MediaType: content.MediaType,
            ContentReference: reference);

    private static AIContent ToContent(
        AgentChatContent content,
        Func<SunderRpcContentReference, ReadOnlyMemory<byte>>? resolveContent)
        => content.Kind switch
        {
            "reasoning" => new TextReasoningContent(content.Text ?? string.Empty) { ProtectedData = content.ProtectedData },
            "text" => new TextContent(content.Text ?? string.Empty),
            "tool-call" => new FunctionCallContent(
                content.CallId ?? string.Empty,
                content.Name ?? string.Empty,
                DeserializeDictionary(content.Json)) { InformationalOnly = content.InformationalOnly },
            "tool-result" => new FunctionResultContent(content.CallId ?? string.Empty, DeserializeObject(content.Json)),
            "usage" => new UsageContent(new UsageDetails
            {
                InputTokenCount = content.Usage?.InputTokens,
                OutputTokenCount = content.Usage?.OutputTokens,
                TotalTokenCount = content.Usage?.TotalTokens,
                CachedInputTokenCount = content.Usage?.CachedInputTokens,
                ReasoningTokenCount = content.Usage?.ReasoningTokens,
            }),
            "content-reference" when content.ContentReference is { } reference && resolveContent is not null
                => new DataContent(resolveContent(reference), reference.MediaType) { Name = reference.FileName },
            "content-reference" when content.ContentUri is { } contentUri
                && Uri.TryCreate(contentUri.Value, UriKind.Absolute, out var uri)
                => new DataContent(uri, content.MediaType ?? "application/octet-stream") { Name = content.Name },
            "error" => new ErrorContent(content.ErrorMessage ?? "Provider error.") { ErrorCode = content.ErrorCode, Details = DeserializeText(content.Json) },
            _ => new ErrorContent("Unsupported Agent RPC chat content."),
        };

    private static IDictionary<string, object?> DeserializeDictionary(SunderRpcOptional<JsonElement> json)
        => !json.TryGetValue(out var value) || value.ValueKind == JsonValueKind.Null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : value.Deserialize<Dictionary<string, object?>>()
              ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    private static object? DeserializeObject(SunderRpcOptional<JsonElement> json)
        => json.TryGetValue(out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : null;

    private static string? DeserializeText(SunderRpcOptional<JsonElement> json)
        => json.TryGetValue(out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static object? DeserializeAdditionalProperty(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.Clone(),
        };
    }

    private static SunderRpcOffsetDateTime ToWireTimestamp(DateTimeOffset value)
        => new(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));

    private static DateTimeOffset FromWireTimestamp(SunderRpcOffsetDateTime value)
        => value.TryGetDateTimeOffset(out var result) ? result
            : throw new SunderRpcException(new(SunderRpcErrorKind.Protocol, "agent.rpc.timestamp-not-representable",
                "The exact RPC date-time cannot be represented by DateTimeOffset."));
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI.Rpc;

#pragma warning disable MEAI001

internal static class ChatValues
{
    internal static async ValueTask<IReadOnlyList<ChatMessage>> MessagesAsync(IReadOnlyList<AgentChatMessage> messages,
        SunderRpcInvocationContext context, CancellationToken token)
    {
        var contents = new Dictionary<SunderRpcContentReference, ReadOnlyMemory<byte>>();
        foreach (var reference in messages.SelectMany(message => message.Contents).Select(content => content.ContentReference).OfType<SunderRpcContentReference>().Distinct())
        {
            if (reference.Length is < 0 or > 32 * 1024 * 1024) throw new InvalidDataException("Chat binary content exceeds the transfer limit.");
            await using var source = await context.OpenContentAsync(reference, token).ConfigureAwait(false);
            var bytes = new byte[checked((int)reference.Length)];
            await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            contents.Add(reference, bytes);
        }
        return messages.Select(message => new ChatMessage(new ChatRole(message.Role), message.Contents.Select(content => Content(content, contents)).ToList())
        { MessageId = message.MessageId, AuthorName = message.AuthorName }).ToArray();
    }

    internal static ChatOptions Options(AgentChatOptions options)
    {
        var result = new ChatOptions
        {
            Instructions = options.Instructions, ConversationId = options.ConversationId, ModelId = options.ModelId,
            Temperature = options.Temperature, MaxOutputTokens = checked((int?)options.MaxOutputTokens), TopP = options.TopP, TopK = checked((int?)options.TopK),
            FrequencyPenalty = options.FrequencyPenalty, PresencePenalty = options.PresencePenalty, Seed = options.Seed,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            Tools = options.Tools.Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.JsonSchema)).ToList(),
        };
        if (Enum.TryParse<ReasoningEffort>(options.ReasoningEffort?.Replace("-", string.Empty), ignoreCase: true, out var effort))
            result.Reasoning = new() { Effort = effort, Output = effort == ReasoningEffort.None ? ReasoningOutput.None : ReasoningOutput.Summary };
        if (options.AdditionalProperties is { Count: > 0 })
        {
            result.AdditionalProperties = new();
            foreach (var pair in options.AdditionalProperties) result.AdditionalProperties[pair.Key] = AdditionalProperty(pair.Value);
        }
        if (options.ContinuationHandle is { Length: > 0 }) result.ContinuationToken = ResponseContinuationToken.FromBytes(options.ContinuationHandle);
        return result;
    }

    internal static AgentChatUpdateEvent Update(ChatResponseUpdate update)
        => new(update.Role?.Value, update.ResponseId, update.MessageId, update.ConversationId, update.ModelId, update.FinishReason?.Value,
            update.CreatedAt is { } createdAt ? new SunderRpcOffsetDateTime(createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)) : null,
            update.Contents.Select(Content).ToArray(), update.ContinuationToken?.ToBytes().ToArray());

    private static AgentChatContent Content(AIContent content)
        => content switch
        {
            TextReasoningContent reasoning => new("reasoning", Text: reasoning.Text, ProtectedData: reasoning.ProtectedData),
            TextContent text => new("text", Text: text.Text),
            FunctionCallContent call => new("tool-call", CallId: call.CallId, Name: call.Name, Json: new(JsonSerializer.SerializeToElement(call.Arguments)), InformationalOnly: call.InformationalOnly),
            FunctionResultContent result => new("tool-result", CallId: result.CallId, Json: new(JsonSerializer.SerializeToElement(result.Result))),
            UsageContent usage => new("usage", Usage: new(usage.Details.InputTokenCount, usage.Details.OutputTokenCount, usage.Details.TotalTokenCount, usage.Details.CachedInputTokenCount, usage.Details.ReasoningTokenCount)),
            ErrorContent error => new("error", ErrorCode: error.ErrorCode, ErrorMessage: error.Message, Json: new(JsonSerializer.SerializeToElement(error.Details))),
            DataContent data => new("content-reference", Name: data.Name, MediaType: data.MediaType, ContentUri: data.Uri is null ? null : new SunderRpcUri(data.Uri)),
            _ => new("error", ErrorCode: "unsupported-content", ErrorMessage: $"Unsupported AI content type '{content.GetType().Name}'."),
        };

    private static AIContent Content(AgentChatContent content, IReadOnlyDictionary<SunderRpcContentReference, ReadOnlyMemory<byte>> references)
        => content.Kind switch
        {
            "reasoning" => new TextReasoningContent(content.Text ?? string.Empty) { ProtectedData = content.ProtectedData },
            "text" => new TextContent(content.Text ?? string.Empty),
            "tool-call" => new FunctionCallContent(content.CallId ?? string.Empty, content.Name ?? string.Empty,
                content.Json.TryGetValue(out var args) && args.ValueKind != JsonValueKind.Null
                    ? args.Deserialize<Dictionary<string, object?>>() ?? new(StringComparer.Ordinal) : new(StringComparer.Ordinal)) { InformationalOnly = content.InformationalOnly },
            "tool-result" => new FunctionResultContent(content.CallId ?? string.Empty, content.Json.TryGetValue(out var result) && result.ValueKind != JsonValueKind.Null ? result.Clone() : null),
            "usage" => ProviderResponseUpdates.CreateUsageContent(new(content.Usage?.InputTokens, content.Usage?.OutputTokens,
                content.Usage?.TotalTokens, content.Usage?.CachedInputTokens, content.Usage?.ReasoningTokens), inferTotalTokenCount: false),
            "content-reference" when content.ContentReference is { } reference => new DataContent(references[reference], reference.MediaType) { Name = reference.FileName },
            "content-reference" when content.ContentUri is { } uri && Uri.TryCreate(uri.Value, UriKind.Absolute, out var parsed)
                => new DataContent(parsed, content.MediaType ?? "application/octet-stream") { Name = content.Name },
            "error" => new ErrorContent(content.ErrorMessage ?? "Provider error.") { ErrorCode = content.ErrorCode,
                Details = content.Json.TryGetValue(out var details) && details.ValueKind != JsonValueKind.Null ? details.ValueKind == JsonValueKind.String ? details.GetString() : details.GetRawText() : null },
            _ => new ErrorContent("Unsupported Agent RPC chat content."),
        };

    private static object? AdditionalProperty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
        JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.Number => value.GetDouble(), _ => value.Clone(),
    };
}

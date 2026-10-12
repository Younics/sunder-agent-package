using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.Shared;

#pragma warning disable MEAI001

internal static class ProviderChatValues
{
    private const int MaximumContentBytes = 32 * 1024 * 1024;

    internal static async ValueTask<IReadOnlyList<ChatMessage>> MessagesAsync(IReadOnlyList<AgentChatMessage> messages,
        CancellationToken token)
    {
        var contents = new Dictionary<SunderContent, ReadOnlyMemory<byte>>(ReferenceEqualityComparer.Instance);
        foreach (var content in messages.SelectMany(message => message.Contents).Select(content => content.Content).OfType<SunderContent>())
        {
            if (contents.ContainsKey(content)) continue;
            if (content.Length is not { } length || length is < 0 or > MaximumContentBytes)
                throw new InvalidDataException("Chat binary content exceeds the transfer limit or has no known length.");
            await using var source = await content.OpenReadAsync(token).ConfigureAwait(false);
            var bytes = new byte[checked((int)length)];
            await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (await source.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Chat binary content exceeds its declared length.");
            contents.Add(content, bytes);
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
        if (options.ReasoningEffort is { } reasoningEffort)
        {
            var effort = reasoningEffort switch
            {
                AgentReasoningEffort.None => ReasoningEffort.None,
                AgentReasoningEffort.Low => ReasoningEffort.Low,
                AgentReasoningEffort.Medium => ReasoningEffort.Medium,
                AgentReasoningEffort.High => ReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh => ReasoningEffort.ExtraHigh,
                _ => throw new InvalidDataException("Unsupported Agent reasoning effort."),
            };
            result.Reasoning = new() { Effort = effort, Output = effort == ReasoningEffort.None ? ReasoningOutput.None : ReasoningOutput.Summary };
        }
        if (options.AdditionalProperties is { Count: > 0 })
        {
            result.AdditionalProperties = new();
            foreach (var pair in options.AdditionalProperties) result.AdditionalProperties[pair.Key] = AdditionalProperty(pair.Value);
        }
        if (options.ContinuationHandle is { Length: > 0 }) result.ContinuationToken = ResponseContinuationToken.FromBytes(options.ContinuationHandle);
        return result;
    }

    internal static async ValueTask<AgentChatUpdateEvent> UpdateAsync(ChatResponseUpdate update)
    {
        var contents = new List<AgentChatContent>(update.Contents.Count);
        try
        {
            foreach (var content in update.Contents) contents.Add(Content(content));
            return new()
            {
                Role = update.Role?.Value,
                ResponseId = update.ResponseId,
                MessageId = update.MessageId,
                ConversationId = update.ConversationId,
                ModelId = update.ModelId,
                FinishReason = update.FinishReason?.Value,
                CreatedAt = update.CreatedAt is { } createdAt
                    ? new SunderRpcOffsetDateTime(createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture))
                    : null,
                ContentItems = contents,
                ContinuationHandle = update.ContinuationToken?.ToBytes().ToArray(),
            };
        }
        catch
        {
            await DisposeContentsAsync(contents).ConfigureAwait(false);
            throw;
        }
    }

    internal static ValueTask DisposeContentAsync(AgentChatStreamEvent? item)
        => item is AgentChatUpdateEvent { ContentItems: { } contents }
            ? DisposeContentsAsync(contents)
            : ValueTask.CompletedTask;

    private static async ValueTask DisposeContentsAsync(IEnumerable<AgentChatContent> contents)
    {
        var disposed = new HashSet<SunderContent>(ReferenceEqualityComparer.Instance);
        foreach (var content in contents.Select(item => item.Content).OfType<SunderContent>())
            if (disposed.Add(content)) await content.DisposeAsync().ConfigureAwait(false);
    }

    private static AgentChatContent Content(AIContent content)
        => content switch
        {
            TextReasoningContent reasoning => Content("reasoning") with { Text = reasoning.Text, ProtectedData = reasoning.ProtectedData },
            TextContent text => Content("text") with { Text = text.Text },
            FunctionCallContent call => Content("tool-call") with { CallId = call.CallId, Name = call.Name, Json = new(JsonSerializer.SerializeToElement(call.Arguments)), InformationalOnly = call.InformationalOnly },
            FunctionResultContent result => Content("tool-result") with { CallId = result.CallId, Json = new(JsonSerializer.SerializeToElement(result.Result)) },
            UsageContent usage => Content("usage") with
            {
                Usage = new()
                {
                    InputTokens = usage.Details.InputTokenCount,
                    OutputTokens = usage.Details.OutputTokenCount,
                    TotalTokens = usage.Details.TotalTokenCount,
                    CachedInputTokens = usage.Details.CachedInputTokenCount,
                    ReasoningTokens = usage.Details.ReasoningTokenCount,
                },
            },
            ErrorContent error => Content("error") with { ErrorCode = error.ErrorCode, ErrorMessage = error.Message, Json = new(JsonSerializer.SerializeToElement(error.Details)) },
            DataContent data when data.Data.Length <= MaximumContentBytes => Content("content-reference") with
            {
                Name = data.Name,
                MediaType = data.MediaType,
                Content = string.IsNullOrWhiteSpace(data.Name)
                    ? SunderContent.FromBytes(data.Data, data.MediaType)
                    : SunderContent.FromBytes(data.Data, data.MediaType, data.Name),
            },
            DataContent => throw new InvalidDataException("Chat binary content exceeds the transfer limit."),
            UriContent uri => Content("content-reference") with { MediaType = uri.MediaType, ContentUri = new SunderRpcUri(uri.Uri.AbsoluteUri) },
            _ => Content("error") with { ErrorCode = "unsupported-content", ErrorMessage = $"Unsupported AI content type '{content.GetType().Name}'." },
        };

    private static AgentChatContent Content(string kind) => new()
    {
        Kind = kind, Text = null, ProtectedData = null, CallId = null, Name = null,
        Json = default, InformationalOnly = false, MediaType = null, ContentUri = null,
        Content = null, Usage = null, ErrorCode = null, ErrorMessage = null,
    };

    private static AIContent Content(AgentChatContent content, IReadOnlyDictionary<SunderContent, ReadOnlyMemory<byte>> references)
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
            "content-reference" when content.Content is { } reference => new DataContent(references[reference], reference.MediaType) { Name = reference.FileName },
            "content-reference" when content.ContentUri is { } dataUri && Uri.TryCreate(dataUri.Value, UriKind.Absolute, out var parsedDataUri)
                && parsedDataUri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
                => new DataContent(parsedDataUri, content.MediaType ?? "application/octet-stream") { Name = content.Name },
            "content-reference" when content.ContentUri is { } uri && Uri.TryCreate(uri.Value, UriKind.Absolute, out var parsed)
                => new UriContent(parsed, content.MediaType ?? "application/octet-stream"),
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

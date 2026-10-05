using System.Text.Json;
using Sunder.Package.Agent.Rpc.Public.Chat;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Rpc.Public.Control;

internal static partial class RunControlMapping
{
    internal static Wire.AgentChatMessage ToDomain(AgentChatMessage value)
        => new(value.Role.Value, Reference(value.MessageId, item => item.Value), Reference(value.AuthorName, item => item.Value), value.Contents.Value.Select(ToDomain).ToArray());

    private static Wire.AgentChatContent ToDomain(AgentChatContent value)
        => new(value.Kind.Value, Reference(value.Text, item => item.Value), Reference(value.ProtectedData, item => item.Value),
            Reference(value.CallId, item => item.Value), Reference(value.Name, item => item.Value),
            value.Json.IsPresent ? new SunderRpcOptional<JsonElement>(value.Json.Value.Value) : default,
            value.InformationalOnly.Value, Reference(value.MediaType, item => item.Value), Nullable(value.ContentUri, item => item.Value),
            Reference(value.ContentReference, item => item.Value), Reference(value.Usage, ToDomain),
            Reference(value.ErrorCode, item => item.Value), Reference(value.ErrorMessage, item => item.Value));

    private static Wire.AgentChatUsage ToDomain(AgentChatUsage value)
        => new(Nullable(value.InputTokens, item => item.Value), Nullable(value.OutputTokens, item => item.Value), Nullable(value.TotalTokens, item => item.Value),
            Nullable(value.CachedInputTokens, item => item.Value), Nullable(value.ReasoningTokens, item => item.Value));

    internal static Wire.AgentChatOptions ToDomain(AgentChatOptions value)
        => new(Reference(value.Instructions, item => item.Value), Reference(value.ConversationId, item => item.Value), Reference(value.ModelId, item => item.Value),
            Nullable(value.Temperature, item => item.Value), Nullable(value.MaxOutputTokens, item => item.Value), Nullable(value.TopP, item => item.Value),
            Nullable(value.TopK, item => item.Value), Nullable(value.FrequencyPenalty, item => item.Value), Nullable(value.PresencePenalty, item => item.Value),
            Nullable(value.Seed, item => item.Value), Nullable(value.AllowMultipleToolCalls, item => item.Value),
            Reference(value.ReasoningEffort, item => JsonNamingPolicy.KebabCaseLower.ConvertName(item.ToString())),
            value.Tools.Value.Select(item => new Wire.AgentChatTool(item.Name.Value, Reference(item.Description, text => text.Value), item.JsonSchema.Value)).ToArray(),
            Reference(value.AdditionalProperties, item => item.Value.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal)),
            Reference(value.ContinuationHandle, item => item.Value));

    internal static AgentChatUpdateEvent FromDomain(Wire.AgentChatUpdateEvent value)
        => new()
        {
            Role = FromReference(value.Role, item => (Identifier)item), ResponseId = FromReference(value.ResponseId, item => (Identifier)item),
            MessageId = FromReference(value.MessageId, item => (Identifier)item), ConversationId = FromReference(value.ConversationId, item => (Identifier)item),
            ModelId = FromReference(value.ModelId, item => (Identifier)item), FinishReason = DisplayText.Optional(value.FinishReason),
            CreatedAt = FromNullable(value.CreatedAt, item => new Timestamp { Value = item }),
            ContentItems = FromReference(value.ContentItems, items => new AgentChatContentCollection { Value = items.Select(FromDomain).ToArray() }),
            ContinuationHandle = FromReference(value.ContinuationHandle, item => new InlineContinuationBytes { Value = item }),
        };

    private static AgentChatContent FromDomain(Wire.AgentChatContent value)
        => new()
        {
            Kind = value.Kind, CallId = FromReference(value.CallId, item => (Identifier)item),
            ContentReference = FromReference(value.ContentReference, item => new ContentReference { Value = item }),
            ContentUri = FromNullable(value.ContentUri, item => new AbsoluteUri { Value = item }),
            ErrorCode = FromReference(value.ErrorCode, item => (TextValue)item), ErrorMessage = DisplayText.Optional(value.ErrorMessage),
            InformationalOnly = value.InformationalOnly,
            Json = value.Json.IsPresent ? new RawJson { Value = value.Json.Value } : default(SunderRpcOptional<RawJson>),
            MediaType = FromReference(value.MediaType, item => new MediaType { Value = item }), Name = FromReference(value.Name, item => (TextValue)item),
            ProtectedData = FromReference(value.ProtectedData, item => new ContentText { Value = item }), Text = FromReference(value.Text, item => new ContentText { Value = item }),
            Usage = FromReference(value.Usage, item => new AgentChatUsage
            {
                InputTokens = FromNullable(item.InputTokens, count => new TokenCount { Value = count }), OutputTokens = FromNullable(item.OutputTokens, count => new TokenCount { Value = count }),
                TotalTokens = FromNullable(item.TotalTokens, count => new TokenCount { Value = count }), CachedInputTokens = FromNullable(item.CachedInputTokens, count => new TokenCount { Value = count }),
                ReasoningTokens = FromNullable(item.ReasoningTokens, count => new TokenCount { Value = count }),
            }),
        };
}

using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public.Chat;

public sealed class AgentChatMessage
{
    public required Identifier Role { get; init; }
    public SunderRpcOptional<Identifier> MessageId { get; init; }
    public SunderRpcOptional<TextValue> AuthorName { get; init; }
    public required ChatContents Contents { get; init; }
}

[SunderRpcTransparent]
public sealed class ChatMessages { public required IReadOnlyList<AgentChatMessage> Value { get; init; } }

public sealed class AgentChatTool
{
    public required TextValue Name { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required RawJsonObject JsonSchema { get; init; }
}

[SunderRpcTransparent]
public sealed class ChatTools { public required IReadOnlyList<AgentChatTool> Value { get; init; } }

public sealed class AgentChatOptions
{
    public SunderRpcOptional<ContentText> Instructions { get; init; }
    public SunderRpcOptional<Identifier> ConversationId { get; init; }
    public SunderRpcOptional<Identifier> ModelId { get; init; }
    public SunderRpcOptional<ChatOptionValue> Temperature { get; init; }
    public SunderRpcOptional<TokenCount> MaxOutputTokens { get; init; }
    public SunderRpcOptional<ChatOptionValue> TopP { get; init; }
    public SunderRpcOptional<TopK> TopK { get; init; }
    public SunderRpcOptional<ChatOptionValue> FrequencyPenalty { get; init; }
    public SunderRpcOptional<ChatOptionValue> PresencePenalty { get; init; }
    public SunderRpcOptional<SafeInteger> Seed { get; init; }
    public SunderRpcOptional<bool> AllowMultipleToolCalls { get; init; }
    public SunderRpcOptional<ChatReasoningEffort> ReasoningEffort { get; init; }
    public required ChatTools Tools { get; init; }
    public SunderRpcOptional<JsonValues> AdditionalProperties { get; init; }
    public SunderRpcOptional<InlineContinuationBytes> ContinuationHandle { get; init; }
}

public sealed class AgentChatStreamRequest
{
    public required Identifier ProviderId { get; init; }
    public required Identifier ModelId { get; init; }
    public required ChatMessages Messages { get; init; }
    public required AgentChatOptions Options { get; init; }
}

public sealed class AgentChatUsage
{
    public SunderRpcOptional<TokenCount> InputTokens { get; init; }
    public SunderRpcOptional<TokenCount> OutputTokens { get; init; }
    public SunderRpcOptional<TokenCount> TotalTokens { get; init; }
    public SunderRpcOptional<TokenCount> CachedInputTokens { get; init; }
    public SunderRpcOptional<TokenCount> ReasoningTokens { get; init; }
}

public sealed class AgentChatContent
{
    [SunderRpcFacet(AllowedStrings = ["reasoning", "text", "tool-call", "tool-result", "usage", "content-reference", "error"])]
    public required string Kind { get; init; }
    public SunderRpcOptional<ContentText> Text { get; init; }
    public SunderRpcOptional<ContentText> ProtectedData { get; init; }
    public SunderRpcOptional<Identifier> CallId { get; init; }
    public SunderRpcOptional<TextValue> Name { get; init; }
    public SunderRpcOptional<RawJson> Json { get; init; }
    public required bool InformationalOnly { get; init; }
    public SunderRpcOptional<MediaType> MediaType { get; init; }
    public SunderRpcOptional<AbsoluteUri> ContentUri { get; init; }
    public SunderRpcOptional<ContentReference> ContentReference { get; init; }
    public SunderRpcOptional<AgentChatUsage> Usage { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public SunderRpcOptional<DisplayText> ErrorMessage { get; init; }
}

[SunderRpcTransparent]
public sealed class ChatContents { public required IReadOnlyList<AgentChatContent> Value { get; init; } }

[SunderRpcUnion(typeof(AgentChatUpdateEvent), typeof(AgentChatErrorEvent)), SunderRpcDiscriminator("kind")]
public abstract record AgentChatStreamEvent;

[SunderRpcVariant("update")]
public sealed record AgentChatUpdateEvent : AgentChatStreamEvent
{
    public SunderRpcOptional<Identifier> Role { get; init; }
    public SunderRpcOptional<Identifier> ResponseId { get; init; }
    public SunderRpcOptional<Identifier> MessageId { get; init; }
    public SunderRpcOptional<Identifier> ConversationId { get; init; }
    public SunderRpcOptional<Identifier> ModelId { get; init; }
    public SunderRpcOptional<DisplayText> FinishReason { get; init; }
    public SunderRpcOptional<Timestamp> CreatedAt { get; init; }
    public SunderRpcOptional<ChatContents> ContentItems { get; init; }
    public SunderRpcOptional<InlineContinuationBytes> ContinuationHandle { get; init; }
}

[SunderRpcVariant("error")]
public sealed record AgentChatErrorEvent : AgentChatStreamEvent
{
    public required TextValue ErrorCode { get; init; }
    public required DisplayText ErrorMessage { get; init; }
    public required ContentText ErrorContent { get; init; }
    public required AgentChatProviderFailureKind ErrorFailureKind { get; init; }
}

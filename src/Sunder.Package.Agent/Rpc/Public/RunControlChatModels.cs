using Sunder.Package.Agent.Rpc.Public.Chat;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public.Control;

public sealed class RunControlChatContext
{
    public SunderRpcOptional<JsonValues> CorrelationAttributes { get; init; }
    public required Identifier ModelId { get; init; }
    public required Identifier ProviderId { get; init; }
}

public sealed class AgentChatContent
{
    public SunderRpcOptional<Identifier> CallId { get; init; }
    public SunderRpcOptional<ContentReference> ContentReference { get; init; }
    public SunderRpcOptional<AbsoluteUri> ContentUri { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public SunderRpcOptional<DisplayText> ErrorMessage { get; init; }
    public required Flag InformationalOnly { get; init; }
    public SunderRpcOptional<RawJson> Json { get; init; }
    public required Identifier Kind { get; init; }
    public SunderRpcOptional<MediaType> MediaType { get; init; }
    public SunderRpcOptional<TextValue> Name { get; init; }
    public SunderRpcOptional<ContentText> ProtectedData { get; init; }
    public SunderRpcOptional<ContentText> Text { get; init; }
    public SunderRpcOptional<AgentChatUsage> Usage { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentChatContentCollection { public required IReadOnlyList<AgentChatContent> Value { get; init; } }

public sealed class AgentChatMessage
{
    public SunderRpcOptional<TextValue> AuthorName { get; init; }
    public required AgentChatContentCollection Contents { get; init; }
    public SunderRpcOptional<Identifier> MessageId { get; init; }
    public required Identifier Role { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentChatMessageCollection { public required IReadOnlyList<AgentChatMessage> Value { get; init; } }

public sealed class AgentChatTool
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required RawJsonObject JsonSchema { get; init; }
    public required TextValue Name { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentChatToolCollection { public required IReadOnlyList<AgentChatTool> Value { get; init; } }

public sealed class AgentChatOptions
{
    public SunderRpcOptional<JsonValues> AdditionalProperties { get; init; }
    public SunderRpcOptional<Flag> AllowMultipleToolCalls { get; init; }
    public SunderRpcOptional<InlineContinuationBytes> ContinuationHandle { get; init; }
    public SunderRpcOptional<Identifier> ConversationId { get; init; }
    public SunderRpcOptional<ChatOptionValue> FrequencyPenalty { get; init; }
    public SunderRpcOptional<ContentText> Instructions { get; init; }
    public SunderRpcOptional<TokenCount> MaxOutputTokens { get; init; }
    public SunderRpcOptional<Identifier> ModelId { get; init; }
    public SunderRpcOptional<ChatOptionValue> PresencePenalty { get; init; }
    public SunderRpcOptional<ChatReasoningEffort> ReasoningEffort { get; init; }
    public SunderRpcOptional<SafeInteger> Seed { get; init; }
    public SunderRpcOptional<ChatOptionValue> Temperature { get; init; }
    public required AgentChatToolCollection Tools { get; init; }
    public SunderRpcOptional<TopK> TopK { get; init; }
    public SunderRpcOptional<ChatOptionValue> TopP { get; init; }
}

public sealed class RunControlChatRequest
{
    public required RunControlChatContext Context { get; init; }
    public required RunControlInvocation Invocation { get; init; }
    public required AgentChatMessageCollection Messages { get; init; }
    public required AgentChatOptions Options { get; init; }
}

[SunderRpcUnion(typeof(AgentChatUpdateEvent), typeof(AgentChatErrorEvent)), SunderRpcDiscriminator("kind")]
public abstract record AgentChatStreamEvent;

[SunderRpcVariant("update")]
public sealed record AgentChatUpdateEvent : AgentChatStreamEvent
{
    public SunderRpcOptional<AgentChatContentCollection> ContentItems { get; init; }
    public SunderRpcOptional<InlineContinuationBytes> ContinuationHandle { get; init; }
    public SunderRpcOptional<Identifier> ConversationId { get; init; }
    public SunderRpcOptional<Timestamp> CreatedAt { get; init; }
    public SunderRpcOptional<DisplayText> FinishReason { get; init; }
    public SunderRpcOptional<Identifier> MessageId { get; init; }
    public SunderRpcOptional<Identifier> ModelId { get; init; }
    public SunderRpcOptional<Identifier> ResponseId { get; init; }
    public SunderRpcOptional<Identifier> Role { get; init; }
}

[SunderRpcVariant("error")]
public sealed record AgentChatErrorEvent : AgentChatStreamEvent
{
    public required TextValue ErrorCode { get; init; }
    public required DisplayText ErrorMessage { get; init; }
    public required ContentText ErrorContent { get; init; }
    public required AgentChatProviderFailureKind ErrorFailureKind { get; init; }
}

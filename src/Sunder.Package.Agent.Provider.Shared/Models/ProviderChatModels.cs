using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

// Package-owned chat values, compiled independently by provider roles. The retained
// Protocol project links this same source while its consumers finish migration.
public sealed record ChatCapabilitiesRequest(string? ModelId);
public sealed record AgentChatStreamRequest(string ProviderId, string ModelId, IReadOnlyList<AgentChatMessage> Messages, AgentChatOptions Options);
public sealed record AgentChatMessage(string Role, string? MessageId, string? AuthorName, IReadOnlyList<AgentChatContent> Contents);
public sealed record AgentChatTool(string Name, string? Description, JsonElement JsonSchema);
public sealed record AgentChatOptions(
    string? Instructions,
    string? ConversationId,
    string? ModelId,
    float? Temperature,
    long? MaxOutputTokens,
    float? TopP,
    long? TopK,
    float? FrequencyPenalty,
    float? PresencePenalty,
    long? Seed,
    bool? AllowMultipleToolCalls,
    string? ReasoningEffort,
    IReadOnlyList<AgentChatTool> Tools,
    IReadOnlyDictionary<string, JsonElement>? AdditionalProperties,
    byte[]? ContinuationHandle);
public sealed record AgentChatContent(
    string Kind,
    string? Text = null,
    string? ProtectedData = null,
    string? CallId = null,
    string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    SunderRpcOptional<JsonElement> Json = default,
    bool InformationalOnly = false,
    string? MediaType = null,
    SunderRpcUri? ContentUri = null,
    SunderRpcContentReference? ContentReference = null,
    AgentChatUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
public sealed record AgentChatUsage(long? InputTokens, long? OutputTokens, long? TotalTokens, long? CachedInputTokens, long? ReasoningTokens);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentChatUpdateEvent), UpdateKind)]
[JsonDerivedType(typeof(AgentChatErrorEvent), ErrorKind)]
public abstract record AgentChatStreamEvent
{
    public const string UpdateKind = "update";
    public const string ErrorKind = "error";

    private protected AgentChatStreamEvent()
    {
    }

    public static AgentChatErrorEvent Error(
        string code,
        string message,
        string content,
        AgentChatProviderFailureKind failureKind)
        => new(code, message, content, failureKind);
}

public sealed record AgentChatUpdateEvent(
    string? Role = null,
    string? ResponseId = null,
    string? MessageId = null,
    string? ConversationId = null,
    string? ModelId = null,
    string? FinishReason = null,
    SunderRpcOffsetDateTime? CreatedAt = null,
    IReadOnlyList<AgentChatContent>? ContentItems = null,
    byte[]? ContinuationHandle = null) : AgentChatStreamEvent
{
    [JsonIgnore]
    public IReadOnlyList<AgentChatContent> Contents => ContentItems ?? [];
}

public sealed record AgentChatErrorEvent(
    string ErrorCode,
    string ErrorMessage,
    string ErrorContent,
    AgentChatProviderFailureKind ErrorFailureKind) : AgentChatStreamEvent;

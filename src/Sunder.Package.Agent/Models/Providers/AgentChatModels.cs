using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Protocol;

// Agent owns these values once. Other packages use generated package Models.
public sealed record AgentChatStreamRequest(string ProviderId, string ModelId, IReadOnlyList<AgentChatMessage> Messages, AgentChatOptions Options);
public sealed record AgentChatMessage(string Role, string? MessageId, string? AuthorName, IReadOnlyList<AgentChatContent> Contents);
public sealed record AgentChatTool(string Name, string? Description,
    [property: SunderValueConstraints(JsonObjectOnly = true)] JsonElement JsonSchema);
public sealed record AgentChatOptions(
    string? Instructions,
    string? ConversationId,
    string? ModelId,
    float? Temperature,
    [property: SunderValueConstraints(Minimum = "0")] long? MaxOutputTokens,
    float? TopP,
    [property: SunderValueConstraints(Minimum = "0")] long? TopK,
    float? FrequencyPenalty,
    float? PresencePenalty,
    long? Seed,
    bool? AllowMultipleToolCalls,
    AgentReasoningEffort? ReasoningEffort,
    IReadOnlyList<AgentChatTool> Tools,
    IReadOnlyDictionary<string, JsonElement>? AdditionalProperties,
    byte[]? ContinuationHandle);
public sealed record AgentChatContent(
    [property: SunderValueConstraints(AllowedStrings = ["reasoning", "text", "tool-call", "tool-result", "usage", "content-reference", "error"])]
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
    SunderContent? Content = null,
    AgentChatUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
public sealed record AgentChatUsage(
    [property: SunderValueConstraints(Minimum = "0")] long? InputTokens,
    [property: SunderValueConstraints(Minimum = "0")] long? OutputTokens,
    [property: SunderValueConstraints(Minimum = "0")] long? TotalTokens,
    [property: SunderValueConstraints(Minimum = "0")] long? CachedInputTokens,
    [property: SunderValueConstraints(Minimum = "0")] long? ReasoningTokens);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentChatUpdateEvent), UpdateKind)]
[JsonDerivedType(typeof(AgentChatErrorEvent), ErrorKind)]
[SunderUnion(typeof(AgentChatUpdateEvent), typeof(AgentChatErrorEvent)), SunderDiscriminator("kind")]
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

[SunderVariant("update")]
public sealed record AgentChatUpdateEvent(
    string? Role = null,
    string? ResponseId = null,
    string? MessageId = null,
    string? ConversationId = null,
    string? ModelId = null,
    string? FinishReason = null,
    SunderRpcOffsetDateTime? CreatedAt = null,
    IReadOnlyList<AgentChatContent>? ContentItems = null,
    byte[]? ContinuationHandle = null) : AgentChatStreamEvent;

[SunderVariant("error")]
public sealed record AgentChatErrorEvent(
    string ErrorCode,
    string ErrorMessage,
    string ErrorContent,
    AgentChatProviderFailureKind ErrorFailureKind) : AgentChatStreamEvent;

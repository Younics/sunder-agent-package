using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct TokenCount
{
    public required long Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct CharacterCount
{
    public required long Value { get; init; }
}

[SunderRpcTransparent]
public sealed class EntityIds
{
    public required IReadOnlyList<EntityId> Value { get; init; }
}

public enum AgentRunStatus
{
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
    [JsonStringEnumMemberName("stopped")] Stopped,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("waiting-for-approval")] WaitingForApproval,
}

public sealed class AgentRunCheckpointRecord
{
    public required EntityId CheckpointId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public required Revision RunRevision { get; init; }
    public required EntityId SessionId { get; init; }
    public required AgentRunStatus Status { get; init; }
    public SunderRpcOptional<DisplayText> Summary { get; init; }
}

public sealed class AgentMemoryConsistencyBarrier
{
    public required Identifier EventId { get; init; }
    public required Sha256Text PayloadHash { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentMemoryConsistencyBarrierCollection
{
    public required IReadOnlyList<AgentMemoryConsistencyBarrier> Value { get; init; }
}

public enum AgentTurnItemKind
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("tool-call")] ToolCall,
    [JsonStringEnumMemberName("tool-result")] ToolResult,
    [JsonStringEnumMemberName("attachment")] Attachment,
}

public enum AgentToolExecutionStatus
{
    [JsonStringEnumMemberName("prepared")] Prepared,
    [JsonStringEnumMemberName("started")] Started,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("ambiguous")] Ambiguous,
}

public sealed class AgentTurnItemRecord
{
    public SunderRpcOptional<JsonText> ArgumentsJson { get; init; }
    public SunderRpcOptional<Identifier> BackendId { get; init; }
    public SunderRpcOptional<Identifier> CallId { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public required Flag IsError { get; init; }
    public required Flag IsToolHeaderProjection { get; init; }
    public required EntityId ItemId { get; init; }
    public required AgentTurnItemKind Kind { get; init; }
    public SunderRpcOptional<JsonText> PresentationPayloadJson { get; init; }
    public SunderRpcOptional<DisplayText> ResultSummary { get; init; }
    public required CollectionIndex SequenceNumber { get; init; }
    public SunderRpcOptional<JsonText> SourcesJson { get; init; }
    public SunderRpcOptional<JsonText> StructuredPayloadJson { get; init; }
    public SunderRpcOptional<ContentText> TextContent { get; init; }
    public required Revision ToolDetailRevision { get; init; }
    public SunderRpcOptional<DisplayText> ToolErrorSummary { get; init; }
    public SunderRpcOptional<EntityId> ToolExecutionId { get; init; }
    public SunderRpcOptional<AgentToolExecutionStatus> ToolExecutionStatus { get; init; }
    public SunderRpcOptional<Flag> ToolHasDetails { get; init; }
    public SunderRpcOptional<DisplayText> ToolHeaderHint { get; init; }
    public SunderRpcOptional<Identifier> ToolId { get; init; }
    public SunderRpcOptional<Identifier> ToolOwnerPackageId { get; init; }
    public SunderRpcOptional<Identifier> ToolSchemaId { get; init; }
    public SunderRpcOptional<VersionText> ToolSchemaVersion { get; init; }
    public required EntityId TurnId { get; init; }
    public required Flag WasTruncated { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentTurnItemRecordCollection
{
    public required IReadOnlyList<AgentTurnItemRecord> Value { get; init; }
}

public enum AgentTurnKind
{
    [JsonStringEnumMemberName("message")] Message,
    [JsonStringEnumMemberName("tool-call")] ToolCall,
    [JsonStringEnumMemberName("tool-result")] ToolResult,
}

public enum AgentMessageRole
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("assistant")] Assistant,
    [JsonStringEnumMemberName("tool")] Tool,
}

public sealed class AgentTurnRecord
{
    public required Revision ContentRevision { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public required Flag IsStreaming { get; init; }
    public required AgentTurnItemRecordCollection Items { get; init; }
    public required AgentTurnKind Kind { get; init; }
    public required AgentMessageRole Role { get; init; }
    public SunderRpcOptional<EntityId> RunId { get; init; }
    public SunderRpcOptional<Revision> RunRevision { get; init; }
    public required EntityId SessionId { get; init; }
    public required EntityId TurnId { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentTurnRecordCollection
{
    public required IReadOnlyList<AgentTurnRecord> Value { get; init; }
}

public sealed class AgentRunContextRecord
{
    public required Flag IsInterrupted { get; init; }
    public required Revision Revision { get; init; }
    public required EntityId RunId { get; init; }
    public required Timestamp StartedAtUtc { get; init; }
    public required AgentRunStatus Status { get; init; }
}

public enum AgentSessionState
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
    [JsonStringEnumMemberName("stopped")] Stopped,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed class AgentSessionContextRecord
{
    public required DisplayText ProfileDisplayName { get; init; }
    public required Identifier ProfileId { get; init; }
    public required EntityId SessionId { get; init; }
    public required AgentSessionState SessionState { get; init; }
    public required DisplayText SessionTitle { get; init; }
    public SunderRpcOptional<DisplayText> WorkingSummary { get; init; }
}

public sealed class AgentSessionRecord
{
    public SunderRpcOptional<TextValue> AgentKind { get; init; }
    public SunderRpcOptional<Identifier> BehaviorLoopId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public SunderRpcOptional<EntityId> ParentRunId { get; init; }
    public SunderRpcOptional<Revision> ParentRunRevision { get; init; }
    public SunderRpcOptional<EntityId> ParentSessionId { get; init; }
    public SunderRpcOptional<Identifier> ParentToolCallId { get; init; }
    public SunderRpcOptional<Identifier> ProfileId { get; init; }
    public SunderRpcOptional<EntityId> RootSessionId { get; init; }
    public required EntityId SessionId { get; init; }
    public required AgentSessionState State { get; init; }
    public SunderRpcOptional<Identifier> TaskId { get; init; }
    public required DisplayText Title { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public SunderRpcOptional<Identifier> WorkspaceId { get; init; }
}

using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentSessionRecord
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> AgentKind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BehaviorLoopId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ParentRunId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> ParentRunRevision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ParentSessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ParentToolCallId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RootSessionId { get; init; }
    public required Uuid SessionId { get; init; }
    public required AgentSessionState State { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> TaskId { get; init; }
    public required string Title { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class AgentSessionSnapshot
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentRunCheckpointRecord> Checkpoint { get; init; }
    public required AgentSessionRecord Session { get; init; }
}

internal sealed class AgentRunCheckpointRecord
{
    public required Uuid CheckpointId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long RunRevision { get; init; }
    public required Uuid SessionId { get; init; }
    public required AgentRunStatus Status { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Summary { get; init; }
}

internal sealed class AgentTurnRecord
{
    [SunderRpcFacet(Minimum = "0")] public required long ContentRevision { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public required bool IsStreaming { get; init; }
    public required IReadOnlyList<AgentTurnItemRecord> Items { get; init; }
    public required AgentTurnKind Kind { get; init; }
    public required AgentMessageRole Role { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RunId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> RunRevision { get; init; }
    public required Uuid SessionId { get; init; }
    public required Uuid TurnId { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

internal sealed class AgentTurnItemRecord
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> ArgumentsJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BackendId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> CallId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ErrorCode { get; init; }
    public required bool IsError { get; init; }
    public required bool IsToolHeaderProjection { get; init; }
    public required Uuid ItemId { get; init; }
    public required AgentTurnItemKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> PresentationPayloadJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ResultSummary { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long SequenceNumber { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> SourcesJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> StructuredPayloadJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> TextContent { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long ToolDetailRevision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolErrorSummary { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ToolExecutionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentToolExecutionStatus> ToolExecutionStatus { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<bool> ToolHasDetails { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolHeaderHint { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolOwnerPackageId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolSchemaId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolSchemaVersion { get; init; }
    public required Uuid TurnId { get; init; }
    public required bool WasTruncated { get; init; }
}

internal sealed class TranscriptPageCursor
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required Uuid TurnId { get; init; }
}

internal sealed class AgentTranscriptPageRequest
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> AnchorCreatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> AnchorTurnId { get; init; }
    public required AgentTranscriptPageDirection Direction { get; init; }
    [SunderRpcFacet(Minimum = "1", Maximum = "500")] public required long Limit { get; init; }
    public required Uuid SessionId { get; init; }
}

internal sealed class AgentTranscriptPage
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<TranscriptPageCursor> Continuation { get; init; }
    public required bool HasMore { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    public required IReadOnlyList<AgentTurnRecord> Turns { get; init; }
}

internal sealed class AgentTranscriptAroundTurnRequest
{
    [SunderRpcFacet(Minimum = "0", Maximum = "30")] public required long AfterLimit { get; init; }
    [SunderRpcFacet(Minimum = "0", Maximum = "30")] public required long BeforeLimit { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ItemId { get; init; }
    public required Uuid SessionId { get; init; }
    public required Uuid TurnId { get; init; }
}

internal sealed class AgentTranscriptAroundTurnPage
{
    public required Uuid AnchorTurnId { get; init; }
    public required bool HasNewer { get; init; }
    public required bool HasOlder { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    public required IReadOnlyList<AgentTurnRecord> Turns { get; init; }
}

internal sealed class AgentTranscriptToolDetailRequest
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> CallId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ItemId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RunId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> RunRevision { get; init; }
    public required Uuid SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ToolExecutionId { get; init; }
}

internal sealed class AgentTranscriptToolDetailResult
{
    public required bool Found { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentTranscriptToolDetailRecord> Detail { get; init; }
}

internal sealed class AgentTranscriptToolDetailRecord
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> ArgumentsJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BackendId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> CallId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> CallItemId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ErrorCode { get; init; }
    public required bool IsError { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> OutputText { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> PresentationPayloadJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ResultItemId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ResultSummary { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RunId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> RunRevision { get; init; }
    public required Uuid SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> SourcesJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentToolExecutionStatus> Status { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> StructuredPayloadJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> ToolExecutionId { get; init; }
    public required string ToolId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolOwnerPackageId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolSchemaId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ToolSchemaVersion { get; init; }
    public required bool WasTransportTruncated { get; init; }
    public required bool WasTruncated { get; init; }
}

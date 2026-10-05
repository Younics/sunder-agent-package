using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Rpc.Public.Catalog;

public sealed class AgentTranscriptToolDetailRequest
{
    public SunderRpcOptional<Identifier> CallId { get; init; }
    public SunderRpcOptional<EntityId> ItemId { get; init; }
    public SunderRpcOptional<EntityId> RunId { get; init; }
    public SunderRpcOptional<Revision> RunRevision { get; init; }
    public required EntityId SessionId { get; init; }
    public SunderRpcOptional<EntityId> ToolExecutionId { get; init; }

    public Domain.AgentTranscriptToolDetailRequest ToDomain()
        => new(SessionId.Value, ToolExecutionId.IsPresent ? ToolExecutionId.Value.Value : null,
            CallId.IsPresent ? CallId.Value.Value : null, ItemId.IsPresent ? ItemId.Value.Value : null,
            RunId.IsPresent ? RunId.Value.Value : null, RunRevision.IsPresent ? RunRevision.Value.Value : null);
}

[SunderRpcTransparent]
public sealed class TranscriptToolDetailLookupResult { public required AgentTranscriptToolDetailRecord? Value { get; init; } }

public sealed class AgentTranscriptToolDetailRecord
{
    public SunderRpcOptional<JsonText> ArgumentsJson { get; init; }
    public SunderRpcOptional<Identifier> BackendId { get; init; }
    public SunderRpcOptional<Identifier> CallId { get; init; }
    public SunderRpcOptional<EntityId> CallItemId { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public required Flag IsError { get; init; }
    public SunderRpcOptional<ContentText> OutputText { get; init; }
    public SunderRpcOptional<JsonText> PresentationPayloadJson { get; init; }
    public SunderRpcOptional<EntityId> ResultItemId { get; init; }
    public SunderRpcOptional<DisplayText> ResultSummary { get; init; }
    public required Revision Revision { get; init; }
    public SunderRpcOptional<EntityId> RunId { get; init; }
    public SunderRpcOptional<Revision> RunRevision { get; init; }
    public required EntityId SessionId { get; init; }
    public SunderRpcOptional<JsonText> SourcesJson { get; init; }
    public SunderRpcOptional<AgentToolExecutionStatus> Status { get; init; }
    public SunderRpcOptional<JsonText> StructuredPayloadJson { get; init; }
    public SunderRpcOptional<EntityId> ToolExecutionId { get; init; }
    public required Identifier ToolId { get; init; }
    public SunderRpcOptional<Identifier> ToolOwnerPackageId { get; init; }
    public SunderRpcOptional<Identifier> ToolSchemaId { get; init; }
    public SunderRpcOptional<VersionText> ToolSchemaVersion { get; init; }
    public required Flag WasTransportTruncated { get; init; }
    public required Flag WasTruncated { get; init; }
}

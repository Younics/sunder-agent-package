using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Rpc.Public.Catalog;

internal static class RuntimeCatalogMapping
{
    internal static AgentSessionRecord? Session(Domain.AgentSessionRecord? value)
        => value is null ? null : new()
        {
            SessionId = value.SessionId, Title = value.Title, State = Enum.Parse<AgentSessionState>(value.State.ToString()),
            CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc), UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc),
            ParentSessionId = Entity(value.ParentSessionId), RootSessionId = Entity(value.RootSessionId),
            ParentRunId = Entity(value.ParentRunId), ParentRunRevision = Rev(value.ParentRunRevision),
            ParentToolCallId = Id(value.ParentToolCallId), TaskId = Id(value.TaskId), ProfileId = Id(value.ProfileId),
            BehaviorLoopId = Id(value.BehaviorLoopId), AgentKind = Text(value.AgentKind), WorkspaceId = Id(value.WorkspaceId),
        };

    internal static AgentProfileRecord? Profile(Domain.AgentProfileRecord? value)
        => value is null ? null : new()
        {
            ProfileId = value.ProfileId, DisplayName = value.DisplayName, Description = DisplayText.Optional(value.Description),
            Instructions = Content(value.Instructions), ChatProviderId = Id(value.ChatProviderId), ChatModelId = Id(value.ChatModelId),
            EmbeddingProviderId = Id(value.EmbeddingProviderId), EmbeddingModelId = Id(value.EmbeddingModelId),
            CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc), UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc),
            BehaviorLoopId = Id(value.BehaviorLoopId), BehaviorLoopSourceId = Id(value.BehaviorLoopSourceId),
            BehaviorLoopSettingsJson = Json(value.BehaviorLoopSettingsJson), IsInternal = value.IsInternal,
            ModelBindings = value.ModelBindings is null ? default(SunderRpcOptional<AgentProfileModelBindingRecordCollection>)
                : new AgentProfileModelBindingRecordCollection { Value = value.ModelBindings.Select(item => ModelBinding(item)!).ToArray() },
            SelectableCapabilityAssignments = value.SelectableCapabilityAssignments is null ? default(SunderRpcOptional<AgentProfileSelectableCapabilityAssignmentRecordCollection>)
                : new AgentProfileSelectableCapabilityAssignmentRecordCollection
                {
                    Value = value.SelectableCapabilityAssignments.Select(item => new AgentProfileSelectableCapabilityAssignmentRecord
                    { CapabilityId = item.CapabilityId, Kind = item.Kind, SourceId = Id(item.SourceId) }).ToArray(),
                },
        };

    internal static AgentProfileModelBindingRecord? ModelBinding(Domain.AgentProfileModelBindingRecord? value)
        => value is null ? null : new()
        {
            CapabilityKind = value.CapabilityKind, ModelId = Id(value.ModelId), ProfileId = value.ProfileId,
            ProviderId = Id(value.ProviderId), SettingsJson = Json(value.SettingsJson), UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc),
        };

    internal static AgentWorkingSummaryRecord? Summary(Domain.AgentWorkingSummaryRecord? value)
        => value is null ? null : new()
        { SessionId = value.SessionId, SummaryText = value.SummaryText, UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc) };

    internal static AgentSessionContextCheckpointRecord? ContextCheckpoint(Domain.AgentSessionContextCheckpointRecord? value)
        => value is null ? null : new()
        {
            ContextCheckpointId = value.ContextCheckpointId, CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc),
            DetailsJson = Json(value.DetailsJson), FirstOmittedTurnId = Entity(value.FirstOmittedTurnId),
            LastOmittedTurnId = Entity(value.LastOmittedTurnId), OmittedTurnCount = new() { Value = value.OmittedTurnCount },
            SessionId = value.SessionId, SummaryText = value.SummaryText,
        };

    internal static AgentRunCheckpointRecord? RunCheckpoint(Domain.AgentRunCheckpointRecord? value)
        => value is null ? null : new()
        {
            CheckpointId = value.CheckpointId, CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc),
            RunRevision = value.RunRevision, SessionId = value.SessionId, Status = Enum.Parse<AgentRunStatus>(value.Status.ToString()),
            Summary = DisplayText.Optional(value.Summary),
        };

    internal static AgentTurnRecord Turn(Domain.AgentTurnRecord value)
        => new()
        {
            ContentRevision = value.ContentRevision, CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc),
            IsStreaming = value.IsStreaming, Items = new() { Value = value.Items.Select(TurnItem).ToArray() },
            Kind = Enum.Parse<AgentTurnKind>(value.Kind.ToString()), Role = Enum.Parse<AgentMessageRole>(value.Role.ToString()),
            RunId = Entity(value.RunId), RunRevision = Rev(value.RunRevision), SessionId = value.SessionId, TurnId = value.TurnId,
            UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc),
        };

    private static AgentTurnItemRecord TurnItem(Domain.AgentTurnItemRecord value)
        => new()
        {
            ArgumentsJson = Json(value.ArgumentsJson), BackendId = Id(value.BackendId), CallId = Id(value.CallId),
            ErrorCode = Text(value.ErrorCode), IsError = value.IsError, IsToolHeaderProjection = value.IsToolHeaderProjection,
            ItemId = value.ItemId, Kind = Enum.Parse<AgentTurnItemKind>(value.Kind.ToString()),
            PresentationPayloadJson = Json(value.PresentationPayloadJson), ResultSummary = DisplayText.Optional(value.ResultSummary),
            SequenceNumber = new() { Value = value.SequenceNumber }, SourcesJson = Json(value.SourcesJson),
            StructuredPayloadJson = Json(value.StructuredPayloadJson), TextContent = Content(value.TextContent),
            ToolDetailRevision = value.ToolDetailRevision, ToolErrorSummary = DisplayText.Optional(value.ToolErrorSummary),
            ToolExecutionId = Entity(value.ToolExecutionId), ToolExecutionStatus = Status(value.ToolExecutionStatus),
            ToolHasDetails = value.ToolHasDetails is { } hasDetails ? new Flag { Value = hasDetails } : default(SunderRpcOptional<Flag>),
            ToolHeaderHint = DisplayText.Optional(value.ToolHeaderHint), ToolId = Id(value.ToolId),
            ToolOwnerPackageId = Id(value.ToolOwnerPackageId), ToolSchemaId = Id(value.ToolSchemaId),
            ToolSchemaVersion = Version(value.ToolSchemaVersion), TurnId = value.TurnId, WasTruncated = value.WasTruncated,
        };

    internal static AgentTranscriptToolDetailRecord? ToolDetail(Domain.AgentTranscriptToolDetailRecord? value)
        => value is null ? null : new()
        {
            ArgumentsJson = Json(value.ArgumentsJson), BackendId = Id(value.BackendId), CallId = Id(value.CallId),
            CallItemId = Entity(value.CallItemId), ErrorCode = Text(value.ErrorCode), IsError = value.IsError,
            OutputText = Content(value.OutputText), PresentationPayloadJson = Json(value.PresentationPayloadJson),
            ResultItemId = Entity(value.ResultItemId), ResultSummary = DisplayText.Optional(value.ResultSummary),
            Revision = value.Revision, RunId = Entity(value.RunId), RunRevision = Rev(value.RunRevision), SessionId = value.SessionId,
            SourcesJson = Json(value.SourcesJson), Status = Status(value.Status), StructuredPayloadJson = Json(value.StructuredPayloadJson),
            ToolExecutionId = Entity(value.ToolExecutionId), ToolId = value.ToolId, ToolOwnerPackageId = Id(value.ToolOwnerPackageId),
            ToolSchemaId = Id(value.ToolSchemaId), ToolSchemaVersion = Version(value.ToolSchemaVersion),
            WasTransportTruncated = value.WasTransportTruncated, WasTruncated = value.WasTruncated,
        };

    private static SunderRpcOptional<Identifier> Id(string? value)
        => value is null ? default(SunderRpcOptional<Identifier>) : new Identifier { Value = value };
    private static SunderRpcOptional<TextValue> Text(string? value)
        => value is null ? default(SunderRpcOptional<TextValue>) : new TextValue { Value = value };
    private static SunderRpcOptional<JsonText> Json(string? value)
        => value is null ? default(SunderRpcOptional<JsonText>) : new JsonText { Value = value };
    private static SunderRpcOptional<ContentText> Content(string? value)
        => value is null ? default(SunderRpcOptional<ContentText>) : new ContentText { Value = value };
    private static SunderRpcOptional<VersionText> Version(string? value)
        => value is null ? default(SunderRpcOptional<VersionText>) : new VersionText { Value = value };
    private static SunderRpcOptional<EntityId> Entity(Guid? value)
        => value is null ? default(SunderRpcOptional<EntityId>) : new EntityId { Value = value.Value };
    private static SunderRpcOptional<Revision> Rev(long? value)
        => value is null ? default(SunderRpcOptional<Revision>) : new Revision { Value = value.Value };
    private static SunderRpcOptional<AgentToolExecutionStatus> Status(Domain.AgentToolExecutionStatus? value)
        => value is null ? default(SunderRpcOptional<AgentToolExecutionStatus>) : Enum.Parse<AgentToolExecutionStatus>(value.Value.ToString());
}

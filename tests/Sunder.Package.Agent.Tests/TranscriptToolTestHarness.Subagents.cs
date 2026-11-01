extern alias SubagentsPackage;

using Sunder.Package.Agent.Contracts.Models;
using SubagentPresentation = SubagentsPackage::Sunder.Package.Agent.Shared.PackageViews;
using SubagentViews = SubagentsPackage::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentModels = SubagentsPackage::Sunder.Generated.Packages.Younics_Ai_Agent_Models;

namespace Sunder.Package.Agent.Tests;

internal static partial class TranscriptToolTestHarness
{
    internal static SubagentViews.SubsessionToolInvocationRowViewModel CreateSubsessionRow(
        object turn,
        object item)
    {
        var localTurn = turn as SubagentModels.AgentTurnRecord ?? SubagentTurn(AgentRoleBoundary.As<AgentTurnRecord>(turn));
        var localItem = item as SubagentModels.AgentTurnItemRecord ?? SubagentItem(AgentRoleBoundary.As<AgentTurnItemRecord>(item));
        var normalizedItem = localItem with { TurnId = localTurn.TurnId };
        var projectedTurn = SubagentPresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(
            localTurn with { Items = [normalizedItem] });
        var projectedItem = projectedTurn.Items.Single();
        var projection = SubagentPresentation.TranscriptRowProjector<SubagentViews.SubsessionTranscriptRowViewModel>
            .DescribeTool(projectedTurn, projectedItem);
        var detail = CreateSubagentDetail(normalizedItem, projection);
        return new SubagentViews.SubsessionToolInvocationRowViewModel(
            projectedTurn,
            projectedItem,
            projection,
            new SubagentPresentation.TranscriptToolPresentationService(),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<SubagentModels.AgentTranscriptToolDetailRecord?>(detail);
            });
    }

    // Immutable UI fixture values only. The imported role owns these generated
    // models; a namespace-based reflection cast cannot turn an Agent source model
    // into one. RPC integration uses the generated clients in the owning tests.
    private static SubagentModels.AgentTurnRecord SubagentTurn(AgentTurnRecord turn) => new()
    {
        TurnId = turn.TurnId,
        TurnSequence = turn.TurnSequence,
        SessionId = turn.SessionId,
        Role = Enum.Parse<SubagentModels.AgentMessageRole>(turn.Role.ToString()),
        Kind = Enum.Parse<SubagentModels.AgentTurnKind>(turn.Kind.ToString()),
        Items = turn.Items.Select(SubagentItem).ToArray(),
        CreatedAtUtc = new(turn.CreatedAtUtc.ToString("O")),
        UpdatedAtUtc = new(turn.UpdatedAtUtc.ToString("O")),
        ContentRevision = turn.ContentRevision,
        IsStreaming = turn.IsStreaming,
        RunId = turn.RunId,
        RunRevision = turn.RunRevision,
    };

    private static SubagentModels.AgentTurnItemRecord SubagentItem(AgentTurnItemRecord item) => new()
    {
        ItemId = item.ItemId,
        TurnId = item.TurnId,
        SequenceNumber = item.SequenceNumber,
        Kind = Enum.Parse<SubagentModels.AgentTurnItemKind>(item.Kind.ToString()),
        TextContent = item.TextContent,
        CallId = item.CallId,
        ToolId = item.ToolId,
        ArgumentsJson = item.ArgumentsJson,
        ResultSummary = item.ResultSummary,
        StructuredPayloadJson = item.StructuredPayloadJson,
        SourcesJson = item.SourcesJson,
        WasTruncated = item.WasTruncated,
        IsError = item.IsError,
        ErrorCode = item.ErrorCode,
        BackendId = item.BackendId,
        PresentationPayloadJson = item.PresentationPayloadJson,
        ToolExecutionId = item.ToolExecutionId,
        ToolExecutionStatus = SubagentStatus(item.ToolExecutionStatus),
        ToolOwnerPackageId = item.ToolOwnerPackageId,
        ToolSchemaId = item.ToolSchemaId,
        ToolSchemaVersion = item.ToolSchemaVersion,
        ToolHeaderHint = item.ToolHeaderHint,
        ToolErrorSummary = item.ToolErrorSummary,
        ToolDetailRevision = item.ToolDetailRevision,
        ToolHasDetails = item.ToolHasDetails,
        IsToolHeaderProjection = item.IsToolHeaderProjection,
    };

    private static SubagentModels.AgentTranscriptToolDetailRecord CreateSubagentDetail(
        SubagentModels.AgentTurnItemRecord item, SubagentPresentation.TranscriptToolProjection projection) => new()
    {
        SessionId = projection.SessionId,
        ToolExecutionId = item.ToolExecutionId,
        CallId = item.CallId,
        CallItemId = item.Kind == SubagentModels.AgentTurnItemKind.ToolCall ? item.ItemId : null,
        ResultItemId = item.Kind == SubagentModels.AgentTurnItemKind.ToolResult ? item.ItemId : null,
        ToolId = item.ToolId ?? "unknown_tool",
        ArgumentsJson = item.ArgumentsJson,
        OutputText = item.TextContent,
        ResultSummary = item.ResultSummary,
        StructuredPayloadJson = item.StructuredPayloadJson,
        SourcesJson = item.SourcesJson,
        PresentationPayloadJson = item.PresentationPayloadJson,
        WasTruncated = item.WasTruncated,
        IsError = item.IsError,
        ErrorCode = item.ErrorCode,
        BackendId = item.BackendId,
        Status = item.ToolExecutionStatus,
        ToolOwnerPackageId = item.ToolOwnerPackageId,
        ToolSchemaId = item.ToolSchemaId,
        ToolSchemaVersion = item.ToolSchemaVersion,
        Revision = projection.DetailRevision,
        WasTransportTruncated = false,
        RunId = projection.RunId,
        RunRevision = projection.RunRevision,
    };

    private static SubagentModels.AgentToolExecutionStatus? SubagentStatus(AgentToolExecutionStatus? status)
        => status is { } value ? Enum.Parse<SubagentModels.AgentToolExecutionStatus>(value.ToString()) : null;

    internal static async Task<SubagentPresentation.TranscriptToolDetailViewModel> ExpandAsync(
        SubagentViews.SubsessionToolInvocationRowViewModel row)
    {
        var request = row.BeginExpansion()
                      ?? throw new InvalidOperationException("The subsession tool row has no details to expand.");
        var detail = await row.LoadDetailsAsync(request, CancellationToken.None);
        if (!row.TryMaterializeDetails(request, detail, out var materialized)
            || materialized is null
            || !row.CommitExpansion(request))
        {
            throw new InvalidOperationException("The subsession tool detail did not materialize.");
        }
        return materialized;
    }

}

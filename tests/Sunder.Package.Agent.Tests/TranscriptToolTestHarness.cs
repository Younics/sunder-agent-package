extern alias AgentCore;
extern alias SubagentsPackage;

using Sunder.Package.Agent.Contracts.Models;
using AgentPresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using AgentServices = AgentCore::Sunder.Package.Agent.Services;
using AgentViews = AgentCore::Sunder.Package.Agent.PackageViews;
using AgentModels = AgentCore::Sunder.Package.Agent.Contracts.Models;
using SubagentPresentation = SubagentsPackage::Sunder.Package.Agent.Shared.PackageViews;
using SubagentViews = SubagentsPackage::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentModels = SubagentsPackage::Sunder.Generated.Packages.Younics_Ai_Agent_Models;

namespace Sunder.Package.Agent.Tests;

internal static class TranscriptToolTestHarness
{
    internal static AgentViews.AgentToolInvocationRowViewModel CreateAgentRow(
        object turn,
        object item,
        AgentServices.AgentToolPresentationService? presentationService = null,
        Func<AgentTranscriptToolDetailRequest, CancellationToken, Task<AgentTranscriptToolDetailRecord?>>? loadDetail = null,
        Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<AgentViews.AgentChildSessionLinkViewModel>>? childSessionLinksResolver = null)
    {
        var localTurn = AgentRoleBoundary.As<AgentModels.AgentTurnRecord>(turn);
        var normalizedItem = AgentRoleBoundary.As<AgentModels.AgentTurnItemRecord>(item) with { TurnId = localTurn.TurnId };
        var projectedTurn = AgentPresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(
            localTurn with { Items = [normalizedItem] });
        var projectedItem = projectedTurn.Items.Single();
        var projection = AgentPresentation.TranscriptRowProjector<AgentViews.AgentTranscriptRowViewModel>
            .DescribeTool(projectedTurn, projectedItem);
        var detail = CreateDetail(AgentRoleBoundary.As<AgentTurnItemRecord>(normalizedItem), projection);
        return new AgentViews.AgentToolInvocationRowViewModel(
            projectedTurn,
            projectedItem,
            projection,
            presentationService ?? new AgentServices.AgentToolPresentationService(),
            loadDetail is null ? ((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<AgentModels.AgentTranscriptToolDetailRecord?>(AgentRoleBoundary.As<AgentModels.AgentTranscriptToolDetailRecord>(detail));
            }) : async (request, token) => AgentRoleBoundary.As<AgentModels.AgentTranscriptToolDetailRecord?>(
                (await loadDetail(AgentRoleBoundary.As<AgentTranscriptToolDetailRequest>(request), token).ConfigureAwait(false))!),
            childSessionLinksResolver is null ? null : (value, row) => childSessionLinksResolver(
                AgentRoleBoundary.As<AgentTurnRecord>(value), AgentRoleBoundary.As<AgentTurnItemRecord>(row)));
    }

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

    internal static async Task<AgentPresentation.TranscriptToolDetailViewModel> ExpandAsync(
        AgentViews.AgentToolInvocationRowViewModel row)
    {
        var request = row.BeginExpansion()
                      ?? throw new InvalidOperationException("The tool row has no details to expand.");
        var detail = await row.LoadDetailsAsync(request, CancellationToken.None);
        if (!row.TryMaterializeDetails(request, detail, out var materialized)
            || materialized is null
            || !row.CommitExpansion(request))
        {
            throw new InvalidOperationException("The Agent tool detail did not materialize.");
        }
        return materialized;
    }

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

    private static AgentTranscriptToolDetailRecord CreateDetail(
        AgentTurnItemRecord item,
        AgentPresentation.TranscriptToolProjection projection)
        => CreateDetail(
            item,
            projection.SessionId,
            projection.DetailRevision,
            projection.RunId,
            projection.RunRevision);

    private static AgentTranscriptToolDetailRecord CreateDetail(
        AgentTurnItemRecord item,
        Guid sessionId,
        long revision,
        Guid? runId,
        long? runRevision)
        => new AgentTranscriptToolDetailRecord(
            sessionId,
            item.ToolExecutionId,
            item.CallId,
            item.Kind == AgentTurnItemKind.ToolCall ? item.ItemId : null,
            item.Kind == AgentTurnItemKind.ToolResult ? item.ItemId : null,
            item.ToolId ?? "unknown_tool",
            item.ArgumentsJson,
            item.TextContent,
            item.ResultSummary,
            item.StructuredPayloadJson,
            item.SourcesJson,
            item.PresentationPayloadJson,
            item.WasTruncated,
            item.IsError,
            item.ErrorCode,
            item.BackendId,
            item.ToolExecutionStatus,
            item.ToolOwnerPackageId,
            item.ToolSchemaId,
            item.ToolSchemaVersion,
            revision)
        {
            RunId = runId,
            RunRevision = runRevision,
        };
}

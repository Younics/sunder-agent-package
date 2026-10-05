extern alias AgentCore;
extern alias SubagentsPackage;

using Sunder.Package.Agent.Contracts.Models;
using AgentPresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using AgentServices = AgentCore::Sunder.Package.Agent.Services;
using AgentViews = AgentCore::Sunder.Package.Agent.PackageViews;
using AgentModels = AgentCore::Sunder.Package.Agent.Contracts.Models;
using SubagentPresentation = SubagentsPackage::Sunder.Package.Agent.Shared.PackageViews;
using SubagentViews = SubagentsPackage::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentModels = SubagentsPackage::Sunder.Package.Agent.Contracts.Models;

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
        var localTurn = AgentRoleBoundary.As<SubagentModels.AgentTurnRecord>(turn);
        var normalizedItem = AgentRoleBoundary.As<SubagentModels.AgentTurnItemRecord>(item) with { TurnId = localTurn.TurnId };
        var projectedTurn = SubagentPresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(
            localTurn with { Items = [normalizedItem] });
        var projectedItem = projectedTurn.Items.Single();
        var projection = SubagentPresentation.TranscriptRowProjector<SubagentViews.SubsessionTranscriptRowViewModel>
            .DescribeTool(projectedTurn, projectedItem);
        var detail = CreateDetail(AgentRoleBoundary.As<AgentTurnItemRecord>(normalizedItem), projection);
        return new SubagentViews.SubsessionToolInvocationRowViewModel(
            projectedTurn,
            projectedItem,
            projection,
            new SubagentPresentation.TranscriptToolPresentationService(),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<SubagentModels.AgentTranscriptToolDetailRecord?>(AgentRoleBoundary.As<SubagentModels.AgentTranscriptToolDetailRecord>(detail));
            });
    }

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
        SubagentPresentation.TranscriptToolProjection projection)
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

extern alias AgentApp;

using Sunder.Package.Agent.Contracts.Models;
using AgentPresentation = AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using AgentServices = AgentApp::Sunder.Package.Agent.Services;
using AgentViews = AgentApp::Sunder.Package.Agent.PackageViews;
using AgentModels = AgentApp::Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

// The Agent-only fixture does not pull a Subagents role into MCP's owning suite.
internal static partial class TranscriptToolTestHarness
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
        var projectedTurn = AgentPresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(localTurn with { Items = [normalizedItem] });
        var projectedItem = projectedTurn.Items.Single();
        var projection = AgentPresentation.TranscriptRowProjector<AgentViews.AgentTranscriptRowViewModel>.DescribeTool(projectedTurn, projectedItem);
        var detail = CreateDetail(AgentRoleBoundary.As<AgentTurnItemRecord>(normalizedItem), projection);
        return new AgentViews.AgentToolInvocationRowViewModel(projectedTurn, projectedItem, projection,
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

    internal static async Task<AgentPresentation.TranscriptToolDetailViewModel> ExpandAsync(AgentViews.AgentToolInvocationRowViewModel row)
    {
        var request = row.BeginExpansion() ?? throw new InvalidOperationException("The tool row has no details to expand.");
        var detail = await row.LoadDetailsAsync(request, CancellationToken.None);
        if (!row.TryMaterializeDetails(request, detail, out var materialized) || materialized is null || !row.CommitExpansion(request))
            throw new InvalidOperationException("The Agent tool detail did not materialize.");
        return materialized;
    }

    private static AgentTranscriptToolDetailRecord CreateDetail(AgentTurnItemRecord item, AgentPresentation.TranscriptToolProjection projection)
        => new(projection.SessionId, item.ToolExecutionId, item.CallId,
            item.Kind == AgentTurnItemKind.ToolCall ? item.ItemId : null,
            item.Kind == AgentTurnItemKind.ToolResult ? item.ItemId : null,
            item.ToolId ?? "unknown_tool", item.ArgumentsJson, item.TextContent, item.ResultSummary,
            item.StructuredPayloadJson, item.SourcesJson, item.PresentationPayloadJson, item.WasTruncated,
            item.IsError, item.ErrorCode, item.BackendId, item.ToolExecutionStatus, item.ToolOwnerPackageId,
            item.ToolSchemaId, item.ToolSchemaVersion, projection.DetailRevision)
        {
            RunId = projection.RunId,
            RunRevision = projection.RunRevision,
        };
}

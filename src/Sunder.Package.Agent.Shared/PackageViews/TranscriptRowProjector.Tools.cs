using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptRowProjector<TRow> where TRow : class
{
    public static TranscriptToolProjection DescribeTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        TranscriptToolDiagnostics.HeaderProjected();
        var status = ResolveToolStatus(item);
        var isFailure = status is "Failed" or "Ambiguous"
                        || item.ToolExecutionStatus is null && item.IsError;
        var kind = isFailure
            ? TranscriptProjectedRowKind.Error
            : item.Kind == AgentTurnItemKind.ToolResult
                ? TranscriptProjectedRowKind.ToolResult
                : TranscriptProjectedRowKind.ToolCall;
        var toolLabel = HumanizeToolName(item.ToolId);
        var headerHint = item.ToolHeaderHint ?? string.Empty;
        var errorSummary = item.ToolErrorSummary ?? string.Empty;
        if (McpToolCardPresentation.TryResolveCardText(
                item.ToolId,
                isFailure ? errorSummary : headerHint,
                isFailure,
                item.WasTruncated,
                out var mcpCard))
        {
            toolLabel = mcpCard.Label;
            if (isFailure)
            {
                errorSummary = mcpCard.DetailText;
            }
            else
            {
                headerHint = mcpCard.DetailText;
            }
        }
        return new TranscriptToolProjection(
            kind,
            turn.SessionId,
            turn.TurnId,
            item.ItemId,
            turn.RunId,
            turn.RunRevision,
            item.ToolExecutionId,
            item.CallId,
            item.ToolId ?? "unknown_tool",
            toolLabel,
            status,
            status == "Completed" ? "✓" : status is "Started" or "Running" ? "i" : status == "Prepared" ? "·" : "!",
            headerHint,
            errorSummary,
            item.ToolDetailRevision > 0
                ? item.ToolDetailRevision
                : TranscriptTurnTransportProjection.CreateDetailRevision(turn),
            TranscriptTurnTransportProjection.HasToolDetails(item),
            TranscriptRowAnchorKey.Tool(turn, item));
    }

    private static string ResolveToolStatus(AgentTurnItemRecord item)
        => item.ToolExecutionStatus?.ToString()
           ?? (item.Kind == AgentTurnItemKind.ToolResult
               ? item.IsError ? "Failed" : "Completed"
               : "Running");
}

using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptRowProjector<TRow> where TRow : class
{
    public static TranscriptToolProjection DescribeTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        TranscriptToolDiagnostics.HeaderProjected();
        var status = ResolveToolStatus(item);
        var kind = status is "Failed" or "Ambiguous"
                   || item.ToolExecutionStatus is null && item.IsError
            ? TranscriptProjectedRowKind.Error
            : item.Kind == AgentTurnItemKind.ToolResult
                ? TranscriptProjectedRowKind.ToolResult
                : TranscriptProjectedRowKind.ToolCall;
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
            HumanizeToolName(item.ToolId),
            status,
            status == "Completed" ? "✓" : status is "Started" or "Running" ? "i" : status == "Prepared" ? "·" : "!",
            item.ToolHeaderHint ?? string.Empty,
            item.ToolErrorSummary ?? string.Empty,
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

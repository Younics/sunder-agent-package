using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptRowProjector<TRow> where TRow : class
{
    private static string HumanizeToolName(string? toolId)
    {
        var parts = (toolId ?? string.Empty)
            .Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? "Tool"
            : string.Join(' ', parts.Select(part =>
                char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    public static TranscriptToolProjection DescribeTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        TranscriptToolDiagnostics.HeaderProjected();
        var state = TranscriptToolStates.Read(item);
        var status = state.ToString();
        var isFailure = state == TranscriptToolState.Failed;
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
            TranscriptToolStates.Icon(state),
            headerHint,
            errorSummary,
            item.ToolDetailRevision > 0
                ? item.ToolDetailRevision
                : Math.Max(1, turn.ContentRevision),
            TranscriptTurnTransportProjection.HasToolDetails(item),
            TranscriptRowAnchorKey.Tool(turn, item))
        {
            State = state,
            IsRunning = state == TranscriptToolState.Running,
        };
    }

}

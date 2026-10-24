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
        var isFailure = status is "Failed" or "Ambiguous" or "Unconfirmed" or "Cancelled"
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
            status is "Completed" or "Applied" ? "✓" : status is "Started" or "Running" or "Running (last observed)" or "Opened" or "Input dispatched" ? "i" : status == "Prepared" ? "·" : "!",
            headerHint,
            errorSummary,
            item.ToolDetailRevision > 0
                ? item.ToolDetailRevision
                : TranscriptTurnTransportProjection.CreateDetailRevision(turn),
            TranscriptTurnTransportProjection.HasToolDetails(item),
            TranscriptRowAnchorKey.Tool(turn, item))
        {
            IsRunning = item.ToolExecutionStatus is AgentToolExecutionStatus.Prepared or AgentToolExecutionStatus.Started
                        || item.ToolExecutionStatus is null && item.Kind == AgentTurnItemKind.ToolCall,
        };
    }

    private static string ResolveToolStatus(AgentTurnItemRecord item)
    {
        var operation = ToolOperationPresentation.Read(item.PresentationPayloadJson);
        // A historical running snapshot cannot hide a terminal execution failure.
        // Later definite observations still describe the external effect separately.
        if (operation == "Running" && item.ToolExecutionStatus is AgentToolExecutionStatus.Failed or AgentToolExecutionStatus.Ambiguous)
            return item.ToolExecutionStatus.ToString()!;
        if (operation == "Running" && item.Kind == AgentTurnItemKind.ToolResult)
            return "Running (last observed)";
        return operation ?? item.ToolExecutionStatus?.ToString()
            ?? (item.Kind == AgentTurnItemKind.ToolResult ? item.IsError ? "Failed" : "Completed" : "Running");
    }
}

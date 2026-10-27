using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Avalonia.Theming;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal enum TranscriptToolState { Running, Completed, Failed }

internal static class TranscriptToolStates
{
    internal static TranscriptToolState Read(AgentTurnItemRecord item)
        => item.ToolExecutionStatus switch
        {
            AgentToolExecutionStatus.Prepared or AgentToolExecutionStatus.Started => TranscriptToolState.Running,
            AgentToolExecutionStatus.Completed => TranscriptToolState.Completed,
            AgentToolExecutionStatus.Failed or AgentToolExecutionStatus.Ambiguous => TranscriptToolState.Failed,
            _ => item.Kind == AgentTurnItemKind.ToolCall ? TranscriptToolState.Running
                : item.IsError ? TranscriptToolState.Failed : TranscriptToolState.Completed,
        };

    internal static string Icon(TranscriptToolState state) => state switch
    {
        TranscriptToolState.Running => "…",
        TranscriptToolState.Completed => "✓",
        _ => "!",
    };

    internal static string Brush(TranscriptToolState state) => state switch
    {
        TranscriptToolState.Running => SunderThemeKeys.WarningBrush,
        TranscriptToolState.Completed => SunderThemeKeys.SuccessBrush,
        _ => SunderThemeKeys.DangerBrush,
    };

    internal static string SoftBrush(TranscriptToolState state) => state switch
    {
        TranscriptToolState.Running => SunderThemeKeys.WarningSoftBrush,
        TranscriptToolState.Completed => SunderThemeKeys.SuccessSoftBrush,
        _ => SunderThemeKeys.DangerSoftBrush,
    };
}

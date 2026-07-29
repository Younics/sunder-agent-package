using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal readonly record struct TranscriptRowAnchorKey(string Value)
{
    public static TranscriptRowAnchorKey Text(Guid turnId) => new($"text:{turnId:N}");

    public static TranscriptRowAnchorKey Tool(AgentTurnRecord turn, AgentTurnItemRecord item)
        => item.ToolExecutionId is { } toolExecutionId
            ? new TranscriptRowAnchorKey($"tool-execution:{toolExecutionId:N}")
            : turn.RunId is { } runId
              && turn.RunRevision is { } runRevision
              && !string.IsNullOrWhiteSpace(item.CallId)
            ? new TranscriptRowAnchorKey($"tool:{runId:N}:{runRevision}:{item.CallId}")
            : new TranscriptRowAnchorKey($"tool:{turn.TurnId:N}:{item.ItemId:N}");

    public static TranscriptRowAnchorKey Activity() => new("activity");

    public static TranscriptRowAnchorKey TailSentinel() => new("$transcript-tail-sentinel");

    public override string ToString() => Value;
}

internal static class TranscriptRowWindow
{
    public static TRow[] SelectRetainedRows<TRow>(
        IReadOnlyList<TRow> visibleRows,
        int retainedRowLimit,
        AgentTranscriptTrimDirection trimDirection,
        object? protectedAnchorKey,
        Func<TRow, object?> anchorKeySelector)
    {
        if (retainedRowLimit <= 0)
        {
            return [];
        }

        if (visibleRows.Count <= retainedRowLimit)
        {
            return visibleRows.ToArray();
        }

        var defaultStart = trimDirection == AgentTranscriptTrimDirection.Oldest
            ? visibleRows.Count - retainedRowLimit
            : 0;
        var protectedIndex = IndexOfAnchor(visibleRows, protectedAnchorKey, anchorKeySelector);
        if (protectedIndex >= defaultStart && protectedIndex < defaultStart + retainedRowLimit)
        {
            return visibleRows.Skip(defaultStart).Take(retainedRowLimit).ToArray();
        }

        if (protectedIndex >= 0)
        {
            var maxStart = visibleRows.Count - retainedRowLimit;
            var desiredStart = trimDirection == AgentTranscriptTrimDirection.Oldest
                ? protectedIndex
                : protectedIndex - retainedRowLimit + 1;
            var start = Math.Clamp(desiredStart, 0, maxStart);
            return visibleRows.Skip(start).Take(retainedRowLimit).ToArray();
        }

        return visibleRows.Skip(defaultStart).Take(retainedRowLimit).ToArray();
    }

    private static int IndexOfAnchor<TRow>(
        IReadOnlyList<TRow> rows,
        object? protectedAnchorKey,
        Func<TRow, object?> anchorKeySelector)
    {
        if (protectedAnchorKey is null)
        {
            return -1;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (Equals(anchorKeySelector(rows[index]), protectedAnchorKey))
            {
                return index;
            }
        }

        return -1;
    }
}

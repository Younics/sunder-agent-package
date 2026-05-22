using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

public readonly record struct TranscriptRowAnchorKey(string Value)
{
    public static TranscriptRowAnchorKey Text(Guid turnId) => new($"text:{turnId:N}");

    public static TranscriptRowAnchorKey Tool(AgentTurnRecord turn, AgentTurnItemRecord item)
        => !string.IsNullOrWhiteSpace(item.CallId)
            ? new TranscriptRowAnchorKey($"tool:{item.CallId}")
            : new TranscriptRowAnchorKey($"tool:{turn.TurnId:N}:{item.ItemId:N}");

    public static TranscriptRowAnchorKey Activity() => new("activity");

    public override string ToString() => Value;
}

public interface ITranscriptRowAnchor
{
    object AnchorKey { get; }
}

internal static class TranscriptRowWindow
{
    public static TRow[] SelectRetainedRows<TRow>(
        IReadOnlyList<TRow> visibleRows,
        int retainedRowLimit,
        AgentTranscriptTrimDirection trimDirection,
        object? protectedAnchorKey)
        where TRow : ITranscriptRowAnchor
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
        var protectedIndex = IndexOfAnchor(visibleRows, protectedAnchorKey);
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

    private static int IndexOfAnchor<TRow>(IReadOnlyList<TRow> rows, object? protectedAnchorKey)
        where TRow : ITranscriptRowAnchor
    {
        if (protectedAnchorKey is null)
        {
            return -1;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (Equals(rows[index].AnchorKey, protectedAnchorKey))
            {
                return index;
            }
        }

        return -1;
    }
}

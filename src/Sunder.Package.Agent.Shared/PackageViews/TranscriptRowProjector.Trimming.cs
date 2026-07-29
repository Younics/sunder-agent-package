using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptRowProjector<TRow>
    where TRow : class
{
    public TranscriptTrimResult EnforceLimit(
        int visibleRowLimit,
        AgentTranscriptTrimDirection trimDirection,
        object? protectedAnchorKey = null)
    {
        var visibleRows = _rows.Where(row => !ReferenceEquals(row, _activityRow)).ToArray();
        var retainedRowLimit = Math.Max(
            0,
            visibleRowLimit - (_activityRow is not null && _rows.Contains(_activityRow) ? 1 : 0));
        if (visibleRows.Length <= retainedRowLimit)
        {
            return TranscriptTrimResult.None;
        }

        var retainedRows = TranscriptRowWindow.SelectRetainedRows(
            visibleRows,
            retainedRowLimit,
            trimDirection,
            protectedAnchorKey,
            _factory.GetAnchorKey);
        if (protectedAnchorKey is not null
            && visibleRows.FirstOrDefault(row => Equals(
                _factory.GetAnchorKey(row),
                protectedAnchorKey)) is { } protectedRow)
        {
            var protectedTurnIds = BuildRetainedTurnIdSet([protectedRow]);
            if (protectedTurnIds.Count > 0)
            {
                var retainedRowSet = retainedRows.ToHashSet(ReferenceEqualityComparer.Instance);
                retainedRows = visibleRows
                    .Where(row => retainedRowSet.Contains(row) || BelongsToTurn(row, protectedTurnIds))
                    .ToArray();
            }
        }

        if (retainedRows.SequenceEqual(visibleRows, ReferenceEqualityComparer.Instance))
        {
            return TranscriptTrimResult.None;
        }

        var retainedTurnIds = BuildRetainedTurnIdSet(retainedRows);
        var retainedTurns = _turnWindow.OrderedTurns()
            .Where(turn => retainedTurnIds.Contains(turn.TurnId))
            .ToArray();
        var retainedAnchorKeys = retainedRows
            .Select(_factory.GetAnchorKey)
            .ToHashSet();
        var firstRetainedIndex = retainedRows.Length == 0
            ? -1
            : Array.FindIndex(visibleRows, row => ReferenceEquals(row, retainedRows[0]));
        var lastRetainedIndex = retainedRows.Length == 0
            ? -1
            : Array.FindLastIndex(visibleRows, row => ReferenceEquals(row, retainedRows[^1]));
        Rebuild(retainedTurns, retainedAnchorKeys);
        if (retainedRows.Length == 0)
        {
            return trimDirection == AgentTranscriptTrimDirection.Oldest
                ? TranscriptTrimResult.Oldest
                : TranscriptTrimResult.Newest;
        }

        var result = TranscriptTrimResult.None;
        if (firstRetainedIndex > 0)
        {
            result |= TranscriptTrimResult.Oldest;
        }
        if (lastRetainedIndex >= 0 && lastRetainedIndex < visibleRows.Length - 1)
        {
            result |= TranscriptTrimResult.Newest;
        }
        return result;
    }

    private bool BelongsToTurn(TRow row, IReadOnlySet<Guid> turnIds)
    {
        var rowId = _factory.GetRowId(row);
        return rowId != Guid.Empty && turnIds.Contains(rowId)
               || _factory.GetResultTurnId(row) is { } resultTurnId && turnIds.Contains(resultTurnId);
    }
}

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal readonly record struct TranscriptViewportAnchorData(
    object? AnchorKey,
    double OffsetY,
    double DistanceFromBottom,
    double? AnchorViewportTop = null);

internal readonly record struct TranscriptLoadTicket(
    Guid SessionId,
    OperationGeneration Generation,
    bool PreserveWindow,
    bool ReconcileAuthoritative,
    long PendingTurnSequence,
    long PendingOverflowRevision);

internal sealed partial class TranscriptTimelineState<TRow> where TRow : class
{
    private static AgentTurnRecord[] SelectFreshestTurns(IEnumerable<AgentTurnRecord> turns)
        => turns
            .GroupBy(turn => turn.TurnId)
            .Select(group => group
                .OrderByDescending(turn => turn.ContentRevision)
                .ThenByDescending(turn => turn.UpdatedAtUtc)
                .First())
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();

    private static int CompareTurnPosition(AgentTurnRecord left, AgentTurnRecord right)
    {
        var timestampComparison = left.CreatedAtUtc.CompareTo(right.CreatedAtUtc);
        return timestampComparison != 0
            ? timestampComparison
            : left.TurnId.CompareTo(right.TurnId);
    }

    private void ApplyPendingTurns(
        Guid sessionId,
        bool scheduleQuietTimer = true)
    {
        var turns = _pendingTurnsById.Values
            .Where(turn => turn.SessionId == sessionId)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        foreach (var turn in turns)
        {
            RemovePendingTurn(turn.TurnId);
            var canApply = _projector.CanApplyTurn(turn);
            _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
            if (canApply)
            {
                TurnProjected?.Invoke(turn, true, scheduleQuietTimer);
            }
        }
        _projector.ReorderRowsChronologically();
    }

    private void ApplyPendingHistoricalUpdates(Guid sessionId)
    {
        var turns = _pendingTurnsById.Values
            .Where(turn => turn.SessionId == sessionId && _projector.CanApplyHistoricalTurnUpdate(turn))
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        foreach (var turn in turns)
        {
            RemovePendingTurn(turn.TurnId);
            var canApply = _projector.CanApplyTurn(turn);
            _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
            if (canApply)
            {
                TurnProjected?.Invoke(turn, true, false);
            }
        }
    }

    private void QueueDetachedTurn(AgentTurnRecord turn)
    {
        var shouldNotify = !HasNewerRows;
        QueuePendingTurn(turn);
        SetHasNewerRows(true);
        if (shouldNotify)
        {
            RowsChanged?.Invoke();
        }
    }

    private void QueuePendingTurn(
        AgentTurnRecord turn,
        bool replaceOnEqualTimestamp = true)
    {
        if (_pendingTurnsById.TryGetValue(turn.TurnId, out var existing)
            && (existing.ContentRevision > turn.ContentRevision
                || existing.ContentRevision == turn.ContentRevision
                && (existing.UpdatedAtUtc > turn.UpdatedAtUtc
                    || !replaceOnEqualTimestamp && existing.UpdatedAtUtc == turn.UpdatedAtUtc)))
        {
            return;
        }

        _pendingTurnsById[turn.TurnId] = turn;
        _pendingTurnSequencesById[turn.TurnId] = ++_pendingTurnSequence;
        while (_pendingTurnsById.Count > _pendingTurnLimit)
        {
            var evictedTurn = _pendingTurnsById.Values
                .OrderBy(item => _projector.CanApplyHistoricalTurnUpdate(item) ? 1 : 0)
                .ThenBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.TurnId)
                .First();
            RemovePendingTurn(evictedTurn.TurnId);
            if (!_projector.CanApplyHistoricalTurnUpdate(evictedTurn))
            {
                _pendingTurnsOverflowed = true;
                _pendingOverflowRevision++;
            }
        }
    }

    private bool RemovePendingTurn(Guid turnId)
    {
        _pendingTurnSequencesById.Remove(turnId);
        return _pendingTurnsById.Remove(turnId);
    }

    private void ClearPendingTurns()
    {
        _pendingTurnsById.Clear();
        _pendingTurnSequencesById.Clear();
    }
}

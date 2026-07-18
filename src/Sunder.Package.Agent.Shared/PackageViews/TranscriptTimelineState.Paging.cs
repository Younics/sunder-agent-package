using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptTimelineState<TRow>
    where TRow : class
{
    public async Task<bool> LoadOlderAsync(
        Func<Guid, DateTimeOffset, Guid, int, CancellationToken, Task<IReadOnlyList<AgentTurnRecord>>> loader,
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanLoadOlder
            || SessionId is not { } sessionId
            || _projector.TurnWindow.OldestCreatedAtUtc is not { } beforeCreatedAt
            || _projector.TurnWindow.OldestTurnId is not { } beforeTurnId)
        {
            return false;
        }

        DetachFromLatest();
        PreserveViewportAnchor(protectedAnchorKey);
        var generation = _pageOperation.Begin("Loading older transcript rows");
        _isLoadingOlder = true;
        NotifyLoadingState();

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                generation.CancellationToken,
                cancellationToken);
            var operationCancellationToken = linkedCancellation.Token;
            var turns = await loader(
                sessionId,
                beforeCreatedAt,
                beforeTurnId,
                _pageSize + 1,
                operationCancellationToken);
            if (SessionId != sessionId || operationCancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (turns.Count == 0)
            {
                SetHasOlderRows(false);
                return false;
            }

            var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
            _isApplyingPageRows = true;
            try
            {
                _projector.ApplyTurns(
                    TranscriptRowProjector<TRow>.SelectLatestTurns(orderedTurns, _pageSize),
                    TranscriptInsertMode.Prepend);
                SetHasOlderRows(orderedTurns.Length > _pageSize);
                ApplyTrim(AgentTranscriptTrimDirection.Newest, protectedAnchorKey);
            }
            finally
            {
                _isApplyingPageRows = false;
            }
            RowsChanged?.Invoke();
            return true;
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested
                                                  || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (_pageOperation.TryComplete(generation))
            {
                _isLoadingOlder = false;
                NotifyLoadingState();
            }
        }
    }

    public async Task<bool> LoadNewerAsync(
        Func<Guid, DateTimeOffset, Guid, int, CancellationToken, Task<IReadOnlyList<AgentTurnRecord>>> loader,
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanLoadNewer
            || SessionId is not { } sessionId
            || _projector.TurnWindow.NewestCreatedAtUtc is not { } afterCreatedAt
            || _projector.TurnWindow.NewestTurnId is not { } afterTurnId)
        {
            return false;
        }

        PreserveViewportAnchor(protectedAnchorKey);
        var generation = _pageOperation.Begin("Loading newer transcript rows");
        var pendingOverflowRevision = _pendingOverflowRevision;
        _isLoadingNewer = true;
        NotifyLoadingState();

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                generation.CancellationToken,
                cancellationToken);
            var operationCancellationToken = linkedCancellation.Token;
            var turns = await loader(
                sessionId,
                afterCreatedAt,
                afterTurnId,
                _pageSize + 1,
                operationCancellationToken);
            if (SessionId != sessionId || operationCancellationToken.IsCancellationRequested)
            {
                return false;
            }
            if (pendingOverflowRevision != _pendingOverflowRevision)
            {
                SetHasNewerRows(true);
                return false;
            }

            var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
            var serverPage = orderedTurns.Take(_pageSize).ToArray();
            var serverHasMore = orderedTurns.Length > _pageSize;
            var pendingTurns = _pendingTurnsById.Values
                .Where(turn => turn.SessionId == sessionId);
            if (serverHasMore && serverPage.LastOrDefault() is { } lastServerTurn)
            {
                pendingTurns = pendingTurns.Where(turn => CompareTurnPosition(turn, lastServerTurn) <= 0);
            }

            var pendingTurnsById = pendingTurns.ToDictionary(turn => turn.TurnId);
            var turnsToApply = SelectFreshestTurns(pendingTurnsById.Values.Concat(serverPage));
            _isApplyingPageRows = true;
            try
            {
                foreach (var turn in turnsToApply)
                {
                    RemovePendingTurn(turn.TurnId);
                    var canApply = _projector.CanApplyTurn(turn);
                    _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
                    if (canApply
                        && pendingTurnsById.TryGetValue(turn.TurnId, out var pendingTurn)
                        && ReferenceEquals(turn, pendingTurn))
                    {
                        TurnProjected?.Invoke(turn, true, true);
                    }
                }
                _projector.ReorderRowsChronologically();

                if (!serverHasMore)
                {
                    _pendingTurnsOverflowed = false;
                }
                SetHasNewerRows(serverHasMore
                                || _pendingTurnsOverflowed
                                || _pendingTurnsById.Values.Any(turn => turn.SessionId == sessionId));
                ApplyTrim(AgentTranscriptTrimDirection.Oldest, protectedAnchorKey);
            }
            finally
            {
                _isApplyingPageRows = false;
            }
            RowsChanged?.Invoke();
            var newestCreatedAt = _projector.TurnWindow.NewestCreatedAtUtc;
            var newestTurnId = _projector.TurnWindow.NewestTurnId;
            var cursorAdvanced = newestCreatedAt > afterCreatedAt
                                 || newestCreatedAt == afterCreatedAt
                                 && newestTurnId is { } newestId
                                 && newestId.CompareTo(afterTurnId) > 0;
            return cursorAdvanced || !HasNewerRows;
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested
                                                  || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (_pageOperation.TryComplete(generation))
            {
                _isLoadingNewer = false;
                NotifyLoadingState();
            }
        }
    }
}

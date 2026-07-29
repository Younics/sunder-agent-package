using Avalonia;
using Avalonia.Threading;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal readonly record struct TranscriptPageCursor(
    DateTimeOffset CreatedAtUtc,
    Guid TurnId)
{
    internal static TranscriptPageCursor FromTurn(AgentTurnRecord turn)
        => new(turn.CreatedAtUtc, turn.TurnId);
}

internal sealed record TranscriptTurnPage(
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasMore,
    TranscriptPageCursor? Continuation = null);

internal sealed partial class TranscriptTimelineState<TRow>
    where TRow : class
{
    public bool TrySelectLoadedAnchor(Guid sessionId, object targetAnchorKey)
    {
        if (_disposed
            || SessionId != sessionId
            || IsInitialLoading
            || _projector.FindByAnchorKey(targetAnchorKey) is null)
        {
            return false;
        }

        _pageOperation.CancelCurrent();
        _isFollowingLatest = false;
        _isJumpToLatestVisible = false;
        _selectedAnchorKey = targetAnchorKey;
        SetViewportAnchor(new TranscriptViewportAnchorData(targetAnchorKey, 0, 0, 28));
        OnPropertyChanged(nameof(IsFollowingLatest));
        OnPropertyChanged(nameof(IsJumpToLatestVisible));
        OnPropertyChanged(nameof(SelectedAnchorKey));
        return true;
    }

    public async Task<bool> LoadOlderAsync(
        Func<Guid, DateTimeOffset, Guid, int, CancellationToken, Task<TranscriptTurnPage>> loader,
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        var cursor = _olderPageCursor ?? GetOldestLoadedCursor();
        if (!CanLoadOlder
            || SessionId is not { } sessionId
            || cursor is not { } before)
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
            var page = await loader(
                sessionId,
                before.CreatedAtUtc,
                before.TurnId,
                _pageSize,
                operationCancellationToken);
            return await RunOnPresentationThreadAsync(
                () => ApplyOlderPage(
                    sessionId,
                    before,
                    page.Turns,
                    page.HasMore,
                    page.Continuation,
                    protectedAnchorKey,
                    operationCancellationToken),
                operationCancellationToken);
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested
                                                  || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            await RunOnPresentationThreadAsync(() =>
            {
                if (_pageOperation.TryComplete(generation))
                {
                    _isLoadingOlder = false;
                    NotifyLoadingState();
                }
            }, CancellationToken.None);
        }
    }

    public async Task<bool> LoadNewerAsync(
        Func<Guid, DateTimeOffset, Guid, int, CancellationToken, Task<TranscriptTurnPage>> loader,
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        var cursor = _newerPageCursor ?? GetNewestLoadedCursor();
        if (!CanLoadNewer
            || SessionId is not { } sessionId
            || cursor is not { } after)
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
            var page = await loader(
                sessionId,
                after.CreatedAtUtc,
                after.TurnId,
                _pageSize,
                operationCancellationToken);
            return await RunOnPresentationThreadAsync(
                () => ApplyNewerPage(
                    sessionId,
                    after,
                    pendingOverflowRevision,
                    page.Turns,
                    page.HasMore,
                    page.Continuation,
                    protectedAnchorKey,
                    operationCancellationToken),
                operationCancellationToken);
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested
                                                  || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            await RunOnPresentationThreadAsync(() =>
            {
                if (_pageOperation.TryComplete(generation))
                {
                    _isLoadingNewer = false;
                    NotifyLoadingState();
                }
            }, CancellationToken.None);
        }
    }

    private bool ApplyOlderPage(
        Guid sessionId,
        TranscriptPageCursor before,
        IReadOnlyList<AgentTurnRecord> turns,
        bool hasMore,
        TranscriptPageCursor? continuation,
        object? protectedAnchorKey,
        CancellationToken cancellationToken)
    {
        if (SessionId != sessionId || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
        var nextCursor = continuation
                         ?? (orderedTurns.FirstOrDefault() is { } oldestTurn
                             ? TranscriptPageCursor.FromTurn(oldestTurn)
                             : (TranscriptPageCursor?)null);
        var cursorAdvanced = nextCursor is { } next && IsBefore(next, before);
        if (turns.Count == 0)
        {
            SetHasOlderRows(hasMore && cursorAdvanced);
            if (cursorAdvanced)
            {
                _olderPageCursor = nextCursor;
            }
            return !hasMore || cursorAdvanced;
        }

        var trimProtectedAnchorKey = ResolvePageProtectedAnchorKey(protectedAnchorKey)
                                     ?? _viewportAnchor?.AnchorKey;
        _isApplyingPageRows = true;
        try
        {
            using var deferredRows = (_projector.Rows as TranscriptObservableCollection<TRow>)?
                .DeferReconciliation();
            _projector.ApplyTurns(
                TranscriptRowProjector<TRow>.SelectLatestTurns(orderedTurns, _pageSize),
                TranscriptInsertMode.Prepend);
            _olderPageCursor = nextCursor;
            SetHasOlderRows(hasMore);
            ApplyTrim(AgentTranscriptTrimDirection.Newest, trimProtectedAnchorKey);
        }
        finally
        {
            _isApplyingPageRows = false;
        }
        RowsChanged?.Invoke();
        return true;
    }

    private bool ApplyNewerPage(
        Guid sessionId,
        TranscriptPageCursor after,
        long pendingOverflowRevision,
        IReadOnlyList<AgentTurnRecord> turns,
        bool hasMore,
        TranscriptPageCursor? continuation,
        object? protectedAnchorKey,
        CancellationToken cancellationToken)
    {
        if (SessionId != sessionId || cancellationToken.IsCancellationRequested)
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
        var nextCursor = continuation
                         ?? (serverPage.LastOrDefault() is { } newestTurn
                             ? TranscriptPageCursor.FromTurn(newestTurn)
                             : (TranscriptPageCursor?)null);
        var pendingTurns = _pendingTurnsById.Values
            .Where(turn => turn.SessionId == sessionId);
        if (hasMore && serverPage.LastOrDefault() is { } lastServerTurn)
        {
            pendingTurns = pendingTurns.Where(turn => CompareTurnPosition(turn, lastServerTurn) <= 0);
        }

        var pendingTurnsById = pendingTurns.ToDictionary(turn => turn.TurnId);
        var turnsToApply = SelectFreshestTurns(pendingTurnsById.Values.Concat(serverPage));
        var trimProtectedAnchorKey = ResolvePageProtectedAnchorKey(protectedAnchorKey)
                                     ?? _viewportAnchor?.AnchorKey;
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
            _newerPageCursor = nextCursor;

            if (!hasMore)
            {
                _pendingTurnsOverflowed = false;
            }
            SetHasNewerRows(hasMore
                            || _pendingTurnsOverflowed
                            || _pendingTurnsById.Values.Any(turn => turn.SessionId == sessionId));
            ApplyTrim(AgentTranscriptTrimDirection.Oldest, trimProtectedAnchorKey);
        }
        finally
        {
            _isApplyingPageRows = false;
        }
        RowsChanged?.Invoke();
        var cursorAdvanced = nextCursor is { } next && IsAfter(next, after);
        return cursorAdvanced || !HasNewerRows;
    }

    private TranscriptPageCursor? GetOldestLoadedCursor()
        => _projector.TurnWindow.OldestCreatedAtUtc is { } createdAt
           && _projector.TurnWindow.OldestTurnId is { } turnId
            ? new TranscriptPageCursor(createdAt, turnId)
            : null;

    private TranscriptPageCursor? GetNewestLoadedCursor()
        => _projector.TurnWindow.NewestCreatedAtUtc is { } createdAt
           && _projector.TurnWindow.NewestTurnId is { } turnId
            ? new TranscriptPageCursor(createdAt, turnId)
            : null;

    private static bool IsBefore(TranscriptPageCursor candidate, TranscriptPageCursor boundary)
        => candidate.CreatedAtUtc < boundary.CreatedAtUtc
           || candidate.CreatedAtUtc == boundary.CreatedAtUtc
           && string.CompareOrdinal(
               candidate.TurnId.ToString("D"),
               boundary.TurnId.ToString("D")) < 0;

    private static bool IsAfter(TranscriptPageCursor candidate, TranscriptPageCursor boundary)
        => candidate.CreatedAtUtc > boundary.CreatedAtUtc
           || candidate.CreatedAtUtc == boundary.CreatedAtUtc
           && string.CompareOrdinal(
               candidate.TurnId.ToString("D"),
               boundary.TurnId.ToString("D")) > 0;

    private static async Task<TResult> RunOnPresentationThreadAsync<TResult>(
        Func<TResult> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        return await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Background,
            cancellationToken);
    }

    private static async Task RunOnPresentationThreadAsync(
        Action action,
        CancellationToken cancellationToken)
        => _ = await RunOnPresentationThreadAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken);
}

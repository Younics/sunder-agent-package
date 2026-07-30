using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptTimelineState<TRow> : INotifyPropertyChanged, IDisposable
    where TRow : class
{
    private readonly TranscriptRowProjector<TRow> _projector;
    private readonly OperationState _initialOperation = new();
    private readonly OperationState _pageOperation = new();
    private readonly Dictionary<Guid, AgentTurnRecord> _pendingTurnsById = [];
    private readonly Dictionary<Guid, long> _pendingTurnSequencesById = [];
    private readonly Dictionary<Guid, (long ContentRevision, DateTimeOffset UpdatedAtUtc)>
        _authoritativeTurnVersions = [];
    private readonly HashSet<object> _expandedAnchorKeys = [];
    private readonly int _initialTurnLimit;
    private readonly int _pageSize;
    private readonly int _visibleRowLimit;
    private readonly int _pendingTurnLimit;
    private bool _hasOlderRows;
    private bool _hasNewerRows;
    private bool _isLoadingOlder;
    private bool _isLoadingNewer;
    private bool _isInitialLoading;
    private bool _isReplacingRows;
    private bool _isApplyingPageRows;
    private bool _isFollowingLatest = true;
    private bool _isJumpToLatestVisible;
    private bool _pendingTurnsOverflowed;
    private bool _disposed;
    private long _pendingOverflowRevision;
    private long _pendingTurnSequence;
    private Guid? _sessionId;
    private object? _selectedAnchorKey;
    private TranscriptViewportAnchorData? _viewportAnchor;
    private TranscriptPageCursor? _olderPageCursor;
    private TranscriptPageCursor? _newerPageCursor;

    public TranscriptTimelineState(
        TranscriptRowProjector<TRow> projector,
        int initialTurnLimit,
        int pageSize,
        int visibleRowLimit)
    {
        _projector = projector;
        _initialTurnLimit = initialTurnLimit;
        _pageSize = pageSize;
        _visibleRowLimit = visibleRowLimit;
        _pendingTurnLimit = Math.Max(pageSize * 2, visibleRowLimit * 2);
        _projector.RowsChanging += OnRowsChanging;
        _projector.RowCreated += OnRowCreated;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<bool>? RowsChanging;

    public event Action? RowsChanged;

    public event Action<AgentTurnRecord, bool, bool>? TurnProjected;

    public Guid? SessionId => _sessionId;

    public bool HasOlderRows => _hasOlderRows;

    public bool HasNewerRows => _hasNewerRows;

    public bool IsLoadingOlder => _isLoadingOlder;

    public bool IsLoadingNewer => _isLoadingNewer;

    public bool IsInitialLoading => _isInitialLoading;

    public bool IsReplacingRows => _isReplacingRows;

    public bool IsFollowingLatest => _isFollowingLatest;

    public bool IsJumpToLatestVisible => _isJumpToLatestVisible;

    public bool CanLoadOlder => HasOlderRows && !IsLoadingOlder && !IsLoadingNewer && !IsInitialLoading && SessionId is not null;

    public bool CanLoadNewer => HasNewerRows && !IsLoadingOlder && !IsLoadingNewer && !IsInitialLoading && SessionId is not null;

    public object? SelectedAnchorKey => _selectedAnchorKey;

    public IReadOnlySet<object> ExpandedAnchorKeys => _expandedAnchorKeys;

    public TranscriptViewportAnchorData? ViewportAnchor => _viewportAnchor;

    public TranscriptRowProjector<TRow> Projector => _projector;

    internal int PendingTurnCount => _pendingTurnsById.Count;

    public TranscriptLoadTicket BeginInitialLoad(
        Guid sessionId,
        bool forceReplacement = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sameSession = _sessionId == sessionId;
        if (!sameSession)
        {
            _authoritativeTurnVersions.Clear();
            _olderPageCursor = null;
            _newerPageCursor = null;
        }
        var preserveReaderState = sameSession && !IsFollowingLatest;
        var preserveWindow = sameSession;
        var reconcileAuthoritative = sameSession
                                     && (forceReplacement || !preserveReaderState);
        var pendingTurnSequence = _pendingTurnSequence;
        var pendingOverflowRevision = _pendingOverflowRevision;
        _pageOperation.CancelCurrent();
        var generation = _initialOperation.Begin("Loading transcript");
        _sessionId = sessionId;
        _isReplacingRows = true;
        _isInitialLoading = true;
        _isLoadingOlder = false;
        _isLoadingNewer = false;
        if (preserveReaderState)
        {
            RowsChanging?.Invoke(false);
        }
        _isFollowingLatest = !preserveReaderState;
        _isJumpToLatestVisible = false;
        if (!preserveWindow)
        {
            _hasOlderRows = false;
            _hasNewerRows = false;
            ClearPendingTurns();
            _pendingTurnsOverflowed = false;
        }
        if (!preserveReaderState)
        {
            _expandedAnchorKeys.Clear();
            _selectedAnchorKey = null;
            _viewportAnchor = null;
        }
        NotifyAllState();
        return new TranscriptLoadTicket(
            sessionId,
            generation,
            preserveWindow,
            reconcileAuthoritative,
            pendingTurnSequence,
            pendingOverflowRevision);
    }

    public TranscriptLoadTicket BeginAnchoredInitialLoad(Guid sessionId)
    {
        var ticket = BeginInitialLoad(sessionId, forceReplacement: true);
        _isFollowingLatest = false;
        OnPropertyChanged(nameof(IsFollowingLatest));
        return ticket;
    }

    public bool TryCompleteAnchoredInitialLoad(
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord> turns,
        bool hasOlderRows,
        bool hasNewerRows,
        object targetAnchorKey)
        => TryCompleteInitialLoad(
            ticket,
            turns,
            hasOlderRows,
            null,
            targetAnchorKey,
            hasNewerRows);

    public bool TryCompleteInitialLoad(
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord> turns,
        bool? hasOlderRows = null,
        TranscriptPageCursor? olderContinuation = null)
        => TryCompleteInitialLoad(ticket, turns, hasOlderRows, olderContinuation, null, null);

    private bool TryCompleteInitialLoad(
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord> turns,
        bool? hasOlderRows,
        TranscriptPageCursor? olderContinuation,
        object? protectedAnchorKey,
        bool? anchoredHasNewerRows)
    {
        if (!IsCurrent(ticket))
        {
            return false;
        }

        if (protectedAnchorKey is not null && anchoredHasNewerRows.HasValue)
        {
            _isFollowingLatest = false;
            _selectedAnchorKey = protectedAnchorKey;
            SetViewportAnchor(new TranscriptViewportAnchorData(protectedAnchorKey, 0, 0, 28));
            OnPropertyChanged(nameof(IsFollowingLatest));
            OnPropertyChanged(nameof(SelectedAnchorKey));
        }

        var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
        if (!ticket.PreserveWindow || ticket.ReconcileAuthoritative)
        {
            _olderPageCursor = olderContinuation
                               ?? (orderedTurns.FirstOrDefault() is { } oldestTurn
                                   ? TranscriptPageCursor.FromTurn(oldestTurn)
                                   : null);
            _newerPageCursor = orderedTurns.LastOrDefault() is { } newestTurn
                ? TranscriptPageCursor.FromTurn(newestTurn)
                : null;
        }
        if (ticket.ReconcileAuthoritative)
        {
            var authoritativeSelectedTurns = TranscriptRowProjector<TRow>.SelectLatestTurns(
                orderedTurns,
                _initialTurnLimit);
            var pendingTurns = _pendingTurnsById.Values
                .Where(turn => turn.SessionId == ticket.SessionId
                               && _pendingTurnSequencesById.GetValueOrDefault(turn.TurnId) > ticket.PendingTurnSequence)
                .ToArray();
            var supersededPendingTurnIds = _pendingTurnsById.Values
                .Where(turn => turn.SessionId == ticket.SessionId
                               && _pendingTurnSequencesById.GetValueOrDefault(turn.TurnId) <= ticket.PendingTurnSequence)
                .Select(turn => turn.TurnId)
                .ToArray();
            foreach (var turnId in supersededPendingTurnIds)
            {
                RemovePendingTurn(turnId);
            }
            if (_pendingOverflowRevision == ticket.PendingOverflowRevision)
            {
                _pendingTurnsOverflowed = false;
            }
            var authoritativeTurnIds = authoritativeSelectedTurns
                .Select(turn => turn.TurnId)
                .ToHashSet();
            var pendingTurnsToReconcile = IsFollowingLatest
                ? pendingTurns
                : pendingTurns
                    .Where(turn => authoritativeTurnIds.Contains(turn.TurnId));
            var authoritativeTurns = SelectFreshestTurns(
                authoritativeSelectedTurns.Concat(pendingTurnsToReconcile));
            foreach (var turn in authoritativeTurns)
            {
                RemovePendingTurn(turn.TurnId);
            }
            _projector.ReconcileAuthoritativeTurns(authoritativeTurns);
            SetHasOlderRows(hasOlderRows ?? orderedTurns.Length > _initialTurnLimit);
            SetHasNewerRows(_pendingTurnsOverflowed || _pendingTurnsById.Count > 0);
            ApplyTrim(AgentTranscriptTrimDirection.Oldest, protectedAnchorKey);
            if (anchoredHasNewerRows is { } reconciledAnchoredHasNewer)
            {
                SetHasNewerRows(HasNewerRows || reconciledAnchoredHasNewer);
            }
            PruneExpandedAnchorKeys();
            _isInitialLoading = false;
            _isReplacingRows = false;
            _initialOperation.TryComplete(ticket.Generation);
            NotifyLoadingState();
            RowsChanged?.Invoke();
            foreach (var turn in authoritativeTurns)
            {
                TurnProjected?.Invoke(turn, false, false);
            }
            return true;
        }

        if (ticket.PreserveWindow)
        {
            var newestLoadedAt = _projector.TurnWindow.NewestCreatedAtUtc;
            var newestLoadedTurnId = _projector.TurnWindow.NewestTurnId;
            var hasNewerSnapshotRows = false;
            foreach (var turn in orderedTurns)
            {
                if (_projector.CanApplyHistoricalTurnUpdate(turn))
                {
                    _projector.ApplyTurn(
                        turn,
                        TranscriptInsertMode.Append,
                        replaceOnEqualTimestamp: false);
                }
                else if (newestLoadedAt is null
                         || turn.CreatedAtUtc > newestLoadedAt
                         || turn.CreatedAtUtc == newestLoadedAt
                         && newestLoadedTurnId is { } newestId
                         && turn.TurnId.CompareTo(newestId) > 0)
                {
                    QueuePendingTurn(turn, replaceOnEqualTimestamp: false);
                    hasNewerSnapshotRows = true;
                }
            }

            ApplyPendingHistoricalUpdates(ticket.SessionId);
            ApplyTrim(
                AgentTranscriptTrimDirection.Oldest,
                protectedAnchorKey ?? _viewportAnchor?.AnchorKey);
            SetHasNewerRows(
                HasNewerRows
                || hasNewerSnapshotRows
                || _pendingTurnsOverflowed
                || _pendingTurnsById.Count > 0);
            if (anchoredHasNewerRows is { } preservedAnchoredHasNewer)
            {
                SetHasNewerRows(HasNewerRows || preservedAnchoredHasNewer);
            }
            _isInitialLoading = false;
            _isReplacingRows = false;
            _initialOperation.TryComplete(ticket.Generation);
            NotifyLoadingState();
            RowsChanged?.Invoke();
            return true;
        }

        var selectedTurns = TranscriptRowProjector<TRow>.SelectLatestTurns(orderedTurns, _initialTurnLimit);
        var pendingTurnsById = IsFollowingLatest
            ? _pendingTurnsById.Values
                .Where(turn => turn.SessionId == ticket.SessionId)
                .ToDictionary(turn => turn.TurnId)
            : new Dictionary<Guid, AgentTurnRecord>();
        var turnsToProject = IsFollowingLatest
            ? SelectFreshestTurns(pendingTurnsById.Values.Concat(selectedTurns))
            : selectedTurns;
        var projectedTurns = new List<(AgentTurnRecord Turn, bool ScheduleQuietTimer)>();
        foreach (var turn in turnsToProject)
        {
            RemovePendingTurn(turn.TurnId);
            projectedTurns.Add((
                turn,
                pendingTurnsById.TryGetValue(turn.TurnId, out var pendingTurn)
                && ReferenceEquals(turn, pendingTurn)));
        }
        _projector.ReconcileAuthoritativeTurns(turnsToProject);

        SetHasOlderRows(hasOlderRows ?? orderedTurns.Length > _initialTurnLimit);
        if (IsFollowingLatest)
        {
            SetHasNewerRows(_pendingTurnsOverflowed);
        }
        else
        {
            ApplyPendingHistoricalUpdates(ticket.SessionId);
            if (_projector.TurnWindow.NewestTurnId is null && !_pendingTurnsOverflowed)
            {
                ApplyPendingTurns(ticket.SessionId, scheduleQuietTimer: false);
            }
            SetHasNewerRows(_pendingTurnsOverflowed || _pendingTurnsById.Count > 0);
        }
        ApplyTrim(AgentTranscriptTrimDirection.Oldest, protectedAnchorKey);
        if (anchoredHasNewerRows is { } projectedAnchoredHasNewer)
        {
            SetHasNewerRows(HasNewerRows || projectedAnchoredHasNewer);
        }
        PruneExpandedAnchorKeys();
        _isInitialLoading = false;
        _isReplacingRows = false;
        _initialOperation.TryComplete(ticket.Generation);
        NotifyLoadingState();
        RowsChanged?.Invoke();
        foreach (var projected in projectedTurns)
        {
            TurnProjected?.Invoke(
                projected.Turn,
                true,
                projected.ScheduleQuietTimer);
        }
        return true;
    }

    public bool TryFailInitialLoad(TranscriptLoadTicket ticket)
    {
        if (!IsCurrent(ticket))
        {
            return false;
        }

        _isInitialLoading = false;
        _isReplacingRows = false;
        ApplyPendingHistoricalUpdates(ticket.SessionId);
        if (IsFollowingLatest)
        {
            if (_pendingTurnsOverflowed)
            {
                SetHasNewerRows(true);
            }
            else
            {
                ApplyPendingTurns(ticket.SessionId);
            }
        }
        else if (_pendingTurnsOverflowed
                 || _pendingTurnsById.Values.Any(turn => turn.SessionId == ticket.SessionId))
        {
            if (_projector.TurnWindow.NewestTurnId is null && !_pendingTurnsOverflowed)
            {
                ApplyPendingTurns(ticket.SessionId, scheduleQuietTimer: false);
            }
            SetHasNewerRows(_pendingTurnsOverflowed || _pendingTurnsById.Count > 0);
        }
        ApplyTrim(
            AgentTranscriptTrimDirection.Oldest,
            IsFollowingLatest ? null : _viewportAnchor?.AnchorKey);
        _initialOperation.TryComplete(ticket.Generation, severity: OperationSeverity.Error);
        NotifyLoadingState();
        RowsChanged?.Invoke();
        return true;
    }

    public void ClearSession()
    {
        _initialOperation.CancelCurrent();
        _pageOperation.CancelCurrent();
        _sessionId = null;
        _olderPageCursor = null;
        _newerPageCursor = null;
        _isReplacingRows = false;
        _isInitialLoading = false;
        _isLoadingOlder = false;
        _isLoadingNewer = false;
        _isFollowingLatest = true;
        _isJumpToLatestVisible = false;
        _hasOlderRows = false;
        _hasNewerRows = false;
        ClearPendingTurns();
        _authoritativeTurnVersions.Clear();
        _pendingTurnsOverflowed = false;
        _expandedAnchorKeys.Clear();
        _selectedAnchorKey = null;
        _viewportAnchor = null;
        _projector.Reset();
        NotifyAllState();
        RowsChanged?.Invoke();
    }

    public TranscriptLiveTurnResult ApplyLiveTurn(AgentTurnRecord turn)
    {
        if (_authoritativeTurnVersions.Remove(turn.TurnId, out var authoritativeVersion)
            && authoritativeVersion.ContentRevision == turn.ContentRevision
            && authoritativeVersion.UpdatedAtUtc == turn.UpdatedAtUtc)
        {
            return TranscriptLiveTurnResult.Ignored;
        }

        return ApplyLiveTurn(turn, notifyVisualChange: true);
    }

    public TranscriptLiveTurnResult ApplyAuthoritativeTurn(AgentTurnRecord turn)
    {
        if (SessionId != turn.SessionId)
        {
            return TranscriptLiveTurnResult.Ignored;
        }

        var oldestTurn = _projector.TurnWindow.OrderedTurns().FirstOrDefault();
        if (!IsInitialLoading
            && !_projector.CanApplyHistoricalTurnUpdate(turn)
            && HasOlderRows
            && oldestTurn is not null
            && CompareTurnPosition(turn, oldestTurn) < 0)
        {
            return TranscriptLiveTurnResult.OutsideWindow;
        }

        var result = ApplyLiveTurn(turn, notifyVisualChange: true);
        if (result == TranscriptLiveTurnResult.Applied)
        {
            _authoritativeTurnVersions[turn.TurnId] = (turn.ContentRevision, turn.UpdatedAtUtc);
        }
        return result;
    }

    private TranscriptLiveTurnResult ApplyLiveTurn(
        AgentTurnRecord turn,
        bool notifyVisualChange)
    {
        if (SessionId != turn.SessionId)
        {
            return TranscriptLiveTurnResult.Ignored;
        }

        if (IsInitialLoading)
        {
            QueuePendingTurn(turn);
            return TranscriptLiveTurnResult.Buffered;
        }

        if (_projector.CanApplyHistoricalTurnUpdate(turn))
        {
            if (!_projector.CanApplyTurn(turn))
            {
                return TranscriptLiveTurnResult.Ignored;
            }

            var insertedRows = _projector.ApplyTurn(
                turn,
                TranscriptInsertMode.Append,
                notifyExistingMessageChange: notifyVisualChange);
            TurnProjected?.Invoke(
                turn,
                notifyVisualChange || turn.IsStreaming,
                IsFollowingLatest);
            if (notifyVisualChange || insertedRows > 0)
            {
                ApplyTrim(
                    AgentTranscriptTrimDirection.Oldest,
                    IsFollowingLatest ? null : _viewportAnchor?.AnchorKey,
                    allowLiveOverflow: IsFollowingLatest);
                RowsChanged?.Invoke();
            }
            return TranscriptLiveTurnResult.Applied;
        }

        if (!IsFollowingLatest)
        {
            if (_projector.TurnWindow.NewestTurnId is null)
            {
                _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
                _projector.ReorderRowsChronologically();
                TurnProjected?.Invoke(turn, true, false);
                ApplyTrim(AgentTranscriptTrimDirection.Oldest);
                RowsChanged?.Invoke();
                return TranscriptLiveTurnResult.Applied;
            }

            QueueDetachedTurn(turn);
            return TranscriptLiveTurnResult.Buffered;
        }

        if (HasNewerRows)
        {
            QueuePendingTurn(turn);
            SetHasNewerRows(true);
            RowsChanged?.Invoke();
            return TranscriptLiveTurnResult.Buffered;
        }

        _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
        _projector.ReorderRowsChronologically();
        TurnProjected?.Invoke(turn, true, true);
        ApplyTrim(AgentTranscriptTrimDirection.Oldest, allowLiveOverflow: true);
        RowsChanged?.Invoke();
        return TranscriptLiveTurnResult.Applied;
    }

    public bool DetachFromLatest()
    {
        if (!IsFollowingLatest || IsReplacingRows || IsInitialLoading || SessionId is null)
        {
            return false;
        }

        _isFollowingLatest = false;
        OnPropertyChanged(nameof(IsFollowingLatest));
        var previousRowCount = _projector.Rows.Count;
        ApplyTrim(AgentTranscriptTrimDirection.Oldest);
        if (_projector.Rows.Count != previousRowCount)
        {
            RowsChanged?.Invoke();
        }
        return true;
    }

    public bool ResumeFollowingLatestIfCaughtUp()
    {
        if (IsFollowingLatest || HasNewerRows || IsInitialLoading || SessionId is null)
        {
            return false;
        }

        _isFollowingLatest = true;
        OnPropertyChanged(nameof(IsFollowingLatest));
        return true;
    }

    public bool RequestJumpToLatest()
    {
        if (SessionId is null)
        {
            return false;
        }

        _isFollowingLatest = true;
        OnPropertyChanged(nameof(IsFollowingLatest));
        return true;
    }

    public void SetJumpToLatestVisible(bool isVisible)
    {
        if (_isJumpToLatestVisible == isVisible)
        {
            return;
        }

        _isJumpToLatestVisible = isVisible;
        OnPropertyChanged(nameof(IsJumpToLatestVisible));
    }

    public void SetViewportAnchor(TranscriptViewportAnchorData? anchor)
    {
        if (Equals(_viewportAnchor, anchor))
        {
            return;
        }

        _viewportAnchor = anchor;
        OnPropertyChanged(nameof(ViewportAnchor));
    }

    public void SelectRow(TRow? row)
    {
        var key = row is null ? null : _projector.GetAnchorKey(row);
        if (Equals(_selectedAnchorKey, key))
        {
            return;
        }

        _selectedAnchorKey = key;
        OnPropertyChanged(nameof(SelectedAnchorKey));
    }

    public void SetRowExpanded(TRow row, bool isExpanded)
    {
        var key = _projector.GetAnchorKey(row);
        if (isExpanded)
        {
            _expandedAnchorKeys.Add(key);
        }
        else
        {
            _expandedAnchorKeys.Remove(key);
        }

        _projector.SetExpanded(row, isExpanded);
        OnPropertyChanged(nameof(ExpandedAnchorKeys));
    }

    public void NotifyRowsChanged() => RowsChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _projector.RowsChanging -= OnRowsChanging;
        _projector.RowCreated -= OnRowCreated;
        _initialOperation.Dispose();
        _pageOperation.Dispose();
        _authoritativeTurnVersions.Clear();
        _projector.Reset();
    }

    private bool IsCurrent(TranscriptLoadTicket ticket)
        => SessionId == ticket.SessionId
           && _initialOperation.TryReport(ticket.Generation);

    private void ApplyTrim(
        AgentTranscriptTrimDirection direction,
        object? protectedAnchorKey = null,
        bool allowLiveOverflow = false)
    {
        if (allowLiveOverflow
            && IsFollowingLatest
            && direction == AgentTranscriptTrimDirection.Oldest
            && _projector.Rows.Count <= _visibleRowLimit + _pageSize)
        {
            return;
        }

        var result = _projector.EnforceLimit(_visibleRowLimit, direction, protectedAnchorKey);
        if (result != TranscriptTrimResult.None)
        {
            PruneExpandedAnchorKeys();
        }

        if (result.HasFlag(TranscriptTrimResult.Oldest))
        {
            _olderPageCursor = null;
            SetHasOlderRows(true);
        }
        if (result.HasFlag(TranscriptTrimResult.Newest))
        {
            _newerPageCursor = null;
            SetHasNewerRows(true);
        }
    }

    private void PruneExpandedAnchorKeys()
    {
        var retainedKeys = _projector.Rows
            .Select(_projector.GetAnchorKey)
            .ToHashSet();
        if (_expandedAnchorKeys.RemoveWhere(key => !retainedKeys.Contains(key)) > 0)
        {
            OnPropertyChanged(nameof(ExpandedAnchorKeys));
        }
    }

    private void OnRowsChanging()
    {
        if (!IsReplacingRows)
        {
            RowsChanging?.Invoke(_isApplyingPageRows);
        }
    }

    private void OnRowCreated(TRow row)
    {
        if (row is ITranscriptToolExpansionOwner owner)
        {
            owner.DetailVisualInvalidated += () => OnDetailVisualInvalidated(row);
        }
        var anchorKey = _projector.GetAnchorKey(row);
        if (_expandedAnchorKeys.Contains(anchorKey))
        {
            _projector.SetExpanded(row, true);
        }
    }

    private void OnDetailVisualInvalidated(TRow row)
    {
        if (_expandedAnchorKeys.Remove(_projector.GetAnchorKey(row)))
        {
            OnPropertyChanged(nameof(ExpandedAnchorKeys));
        }
    }

    private void SetHasOlderRows(bool value)
    {
        if (_hasOlderRows == value)
        {
            return;
        }

        _hasOlderRows = value;
        OnPropertyChanged(nameof(HasOlderRows));
        OnPropertyChanged(nameof(CanLoadOlder));
    }

    private void SetHasNewerRows(bool value)
    {
        if (_hasNewerRows == value)
        {
            return;
        }

        _hasNewerRows = value;
        OnPropertyChanged(nameof(HasNewerRows));
        OnPropertyChanged(nameof(CanLoadNewer));
    }

    private void NotifyAllState()
    {
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(HasOlderRows));
        OnPropertyChanged(nameof(HasNewerRows));
        OnPropertyChanged(nameof(IsFollowingLatest));
        OnPropertyChanged(nameof(IsJumpToLatestVisible));
        OnPropertyChanged(nameof(SelectedAnchorKey));
        OnPropertyChanged(nameof(ExpandedAnchorKeys));
        OnPropertyChanged(nameof(ViewportAnchor));
        NotifyLoadingState();
    }

    private void NotifyLoadingState()
    {
        OnPropertyChanged(nameof(IsLoadingOlder));
        OnPropertyChanged(nameof(IsLoadingNewer));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsReplacingRows));
        OnPropertyChanged(nameof(CanLoadOlder));
        OnPropertyChanged(nameof(CanLoadNewer));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

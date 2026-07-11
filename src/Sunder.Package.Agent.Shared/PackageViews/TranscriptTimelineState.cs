using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal readonly record struct TranscriptViewportAnchorData(
    object? AnchorKey,
    double OffsetY,
    double DistanceFromBottom);

internal readonly record struct TranscriptLoadTicket(
    Guid SessionId,
    OperationGeneration Generation);

internal sealed class TranscriptTimelineState<TRow> : INotifyPropertyChanged, IDisposable
    where TRow : class
{
    private readonly TranscriptRowProjector<TRow> _projector;
    private readonly OperationState _initialOperation = new();
    private readonly OperationState _pageOperation = new();
    private readonly Dictionary<Guid, AgentTurnRecord> _pendingTurnsById = [];
    private readonly HashSet<object> _expandedAnchorKeys = [];
    private readonly int _initialTurnLimit;
    private readonly int _pageSize;
    private readonly int _visibleRowLimit;
    private bool _hasOlderRows;
    private bool _hasNewerRows;
    private bool _isLoadingOlder;
    private bool _isLoadingNewer;
    private bool _isInitialLoading;
    private bool _isReplacingRows;
    private bool _isFollowingLatest = true;
    private bool _isJumpToLatestVisible;
    private bool _disposed;
    private Guid? _sessionId;
    private object? _selectedAnchorKey;
    private TranscriptViewportAnchorData? _viewportAnchor;

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
        _projector.RowsChanging += OnRowsChanging;
        _projector.RowCreated += OnRowCreated;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? RowsChanging;

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

    public TranscriptLoadTicket BeginInitialLoad(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pageOperation.CancelCurrent();
        var generation = _initialOperation.Begin("Loading transcript");
        _sessionId = sessionId;
        _isReplacingRows = true;
        _isInitialLoading = true;
        _isLoadingOlder = false;
        _isLoadingNewer = false;
        _isFollowingLatest = true;
        _isJumpToLatestVisible = false;
        _hasOlderRows = false;
        _hasNewerRows = false;
        _pendingTurnsById.Clear();
        _expandedAnchorKeys.Clear();
        _selectedAnchorKey = null;
        _viewportAnchor = null;
        _projector.Reset();
        NotifyAllState();
        RowsChanged?.Invoke();
        return new TranscriptLoadTicket(sessionId, generation);
    }

    public bool TryCompleteInitialLoad(
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        if (!IsCurrent(ticket))
        {
            return false;
        }

        var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
        foreach (var turn in TranscriptRowProjector<TRow>.SelectLatestTurns(orderedTurns, _initialTurnLimit))
        {
            _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
            TurnProjected?.Invoke(turn, true, false);
        }

        ApplyPendingTurns(ticket.SessionId);
        SetHasOlderRows(orderedTurns.Length > _initialTurnLimit);
        SetHasNewerRows(false);
        ApplyTrim(AgentTranscriptTrimDirection.Oldest);
        _isInitialLoading = false;
        _isReplacingRows = false;
        _initialOperation.TryComplete(ticket.Generation);
        NotifyLoadingState();
        RowsChanged?.Invoke();
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
        _isReplacingRows = false;
        _isInitialLoading = false;
        _isLoadingOlder = false;
        _isLoadingNewer = false;
        _isFollowingLatest = true;
        _isJumpToLatestVisible = false;
        _hasOlderRows = false;
        _hasNewerRows = false;
        _pendingTurnsById.Clear();
        _expandedAnchorKeys.Clear();
        _selectedAnchorKey = null;
        _viewportAnchor = null;
        _projector.Reset();
        NotifyAllState();
        RowsChanged?.Invoke();
    }

    public async Task<bool> LoadOlderAsync(
        Func<Guid, DateTimeOffset, Guid, int, CancellationToken, Task<IReadOnlyList<AgentTurnRecord>>> loader,
        object? protectedAnchorKey = null)
    {
        if (!CanLoadOlder
            || SessionId is not { } sessionId
            || _projector.TurnWindow.OldestCreatedAtUtc is not { } beforeCreatedAt
            || _projector.TurnWindow.OldestTurnId is not { } beforeTurnId)
        {
            return false;
        }

        DetachFromLatest();
        SetViewportAnchor(new TranscriptViewportAnchorData(protectedAnchorKey, 0, 0));
        var generation = _pageOperation.Begin("Loading older transcript rows");
        _isLoadingOlder = true;
        NotifyLoadingState();

        IReadOnlyList<AgentTurnRecord> turns;
        try
        {
            turns = await loader(
                sessionId,
                beforeCreatedAt,
                beforeTurnId,
                _pageSize + 1,
                generation.CancellationToken);
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
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

        if (SessionId != sessionId || generation.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (turns.Count == 0)
        {
            SetHasOlderRows(false);
            return false;
        }

        var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
        _projector.ApplyTurns(
            TranscriptRowProjector<TRow>.SelectLatestTurns(orderedTurns, _pageSize),
            TranscriptInsertMode.Prepend);
        SetHasOlderRows(orderedTurns.Length > _pageSize);
        ApplyTrim(AgentTranscriptTrimDirection.Newest, protectedAnchorKey);
        RowsChanged?.Invoke();
        return true;
    }

    public async Task<bool> LoadNewerAsync(
        Func<Guid, DateTimeOffset, Guid, int, CancellationToken, Task<IReadOnlyList<AgentTurnRecord>>> loader,
        object? protectedAnchorKey = null)
    {
        if (!CanLoadNewer
            || SessionId is not { } sessionId
            || _projector.TurnWindow.NewestCreatedAtUtc is not { } afterCreatedAt
            || _projector.TurnWindow.NewestTurnId is not { } afterTurnId)
        {
            return false;
        }

        SetViewportAnchor(new TranscriptViewportAnchorData(protectedAnchorKey, 0, 0));
        var generation = _pageOperation.Begin("Loading newer transcript rows");
        _isLoadingNewer = true;
        NotifyLoadingState();

        IReadOnlyList<AgentTurnRecord> turns;
        try
        {
            turns = await loader(
                sessionId,
                afterCreatedAt,
                afterTurnId,
                _pageSize + 1,
                generation.CancellationToken);
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
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

        if (SessionId != sessionId || generation.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var orderedTurns = TranscriptRowProjector<TRow>.OrderTurns(turns);
        if (orderedTurns.Length > 0)
        {
            foreach (var turn in orderedTurns.Take(_pageSize))
            {
                _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
                _pendingTurnsById.Remove(turn.TurnId);
            }
        }

        SetHasNewerRows(orderedTurns.Length > _pageSize);
        ApplyTrim(AgentTranscriptTrimDirection.Oldest, protectedAnchorKey);
        if (!HasNewerRows)
        {
            ApplyPendingTurns(sessionId);
            ApplyTrim(AgentTranscriptTrimDirection.Oldest, protectedAnchorKey);
            ResumeFollowingLatestIfCaughtUp();
        }

        RowsChanged?.Invoke();
        return orderedTurns.Length > 0 || !HasNewerRows;
    }

    public TranscriptLiveTurnResult ApplyLiveTurn(AgentTurnRecord turn)
    {
        if (SessionId != turn.SessionId)
        {
            return TranscriptLiveTurnResult.Ignored;
        }

        if (IsInitialLoading)
        {
            _pendingTurnsById[turn.TurnId] = turn;
            return TranscriptLiveTurnResult.Buffered;
        }

        if (!IsFollowingLatest)
        {
            QueueDetachedTurn(turn);
            return TranscriptLiveTurnResult.Buffered;
        }

        if (HasNewerRows && !_projector.CanApplyHistoricalTurnUpdate(turn))
        {
            SetHasNewerRows(true);
            RowsChanged?.Invoke();
            return TranscriptLiveTurnResult.Buffered;
        }

        _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
        TurnProjected?.Invoke(turn, true, true);
        ApplyTrim(AgentTranscriptTrimDirection.Oldest);
        RowsChanged?.Invoke();
        return TranscriptLiveTurnResult.Applied;
    }

    public void ApplyActivity(string text, bool isReasoning, bool isVisible)
    {
        if (!IsFollowingLatest && !IsReplacingRows)
        {
            return;
        }

        _projector.SetActivity(text, isReasoning, isVisible);
        ApplyTrim(AgentTranscriptTrimDirection.Oldest);
    }

    public void DetachFromLatest()
    {
        if (!IsFollowingLatest || IsReplacingRows || IsInitialLoading || SessionId is null)
        {
            return;
        }

        _isFollowingLatest = false;
        OnPropertyChanged(nameof(IsFollowingLatest));
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
        _projector.Reset();
    }

    private bool IsCurrent(TranscriptLoadTicket ticket)
        => SessionId == ticket.SessionId
           && _initialOperation.TryReport(ticket.Generation);

    private void ApplyPendingTurns(Guid sessionId)
    {
        var turns = _pendingTurnsById.Values
            .Where(turn => turn.SessionId == sessionId)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        foreach (var turn in turns)
        {
            _pendingTurnsById.Remove(turn.TurnId);
            _projector.ApplyTurn(turn, TranscriptInsertMode.Append);
            TurnProjected?.Invoke(turn, true, true);
        }
    }

    private void QueueDetachedTurn(AgentTurnRecord turn)
    {
        var shouldNotify = !HasNewerRows;
        _pendingTurnsById[turn.TurnId] = turn;
        SetHasNewerRows(true);
        if (shouldNotify)
        {
            RowsChanged?.Invoke();
        }
    }

    private void ApplyTrim(
        AgentTranscriptTrimDirection direction,
        object? protectedAnchorKey = null)
    {
        switch (_projector.EnforceLimit(_visibleRowLimit, direction, protectedAnchorKey))
        {
            case TranscriptTrimResult.Oldest:
                SetHasOlderRows(true);
                break;
            case TranscriptTrimResult.Newest:
                SetHasNewerRows(true);
                break;
        }
    }

    private void OnRowsChanging()
    {
        if (!IsReplacingRows)
        {
            RowsChanging?.Invoke();
        }
    }

    private void OnRowCreated(TRow row)
    {
        var anchorKey = _projector.GetAnchorKey(row);
        if (_expandedAnchorKeys.Contains(anchorKey))
        {
            _projector.SetExpanded(row, true);
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

internal enum TranscriptLiveTurnResult
{
    Ignored,
    Buffered,
    Applied,
}

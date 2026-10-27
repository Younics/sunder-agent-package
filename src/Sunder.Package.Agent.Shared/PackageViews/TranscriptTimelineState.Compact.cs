using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptTimelineState<TRow> where TRow : class
{
    private TranscriptCompactWindow<TRow>? _compact;
    private Func<AgentCompactTranscriptRequest, CancellationToken, Task<AgentCompactTranscriptPage>>? _loadCompact;
    private SerializedRefreshLoop? _compactRefresh;
    private bool _compactRefreshPending;
    private long _compactRevision;
    private (object Source, object Target)? _modeAnchorMapping;

    internal bool IsCompact { get; private set; }
    internal bool SupportsCompact => _compact is not null;
    internal bool HasCompactRunningTools => IsCompact && _compact?.HasRunningTools == true;
    internal event Action<Exception>? CompactLoadFailed;
    internal Task PendingCompactOperations => WaitForCompactOperationsAsync();

    private async Task WaitForCompactOperationsAsync()
    {
        while (true)
        {
            var pending = await RunOnPresentationThreadAsync(() =>
                (Refresh: _compactRefresh?.WhenIdle ?? Task.CompletedTask,
                    Loads: _compact?.PendingLoads ?? Task.CompletedTask), CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(pending.Refresh, pending.Loads).ConfigureAwait(false);
            // Applying a summary can start a new member snapshot after the first
            // task capture. Snapshot owner-thread state again before declaring
            // the transcript idle; do not inspect its mutable entries off-thread.
            if (await RunOnPresentationThreadAsync(() =>
                    ReferenceEquals(pending.Refresh, _compactRefresh?.WhenIdle ?? Task.CompletedTask)
                    && (_compact?.PendingLoads.IsCompletedSuccessfully ?? true), CancellationToken.None).ConfigureAwait(false)) return;
        }
    }

    internal void ReconcileCompactSnapshot(AgentCompactTranscriptPage page)
    {
        if (_compact?.Matches(page) != true) RequestCompactRefresh();
    }

    internal void ConfigureCompact(
        Func<AgentCompactTranscriptRequest, CancellationToken, Task<AgentCompactTranscriptPage>> load,
        Func<AgentTranscriptActivityMembersRequest, CancellationToken, Task<AgentTranscriptActivityMembersPage>> members)
    {
        _loadCompact = load;
        _compact = new(_projector, members, (action, token) => RunOnPresentationThreadAsync(action, token));
        _compact.Changed += OnCompactRowsChanged;
        _compactRefresh = new(RefreshCompactWindowAsync, exception => CompactLoadFailed?.Invoke(exception));
    }

    internal void SetCompactMode(bool compact)
    {
        compact &= SupportsCompact;
        if (IsCompact == compact) return;
        _modeAnchorMapping = null;
        _compactRefresh?.DiscardPending();
        _pageOperation.CancelCurrent();
        _initialOperation.CancelCurrent();
        _compact!.Reset();
        _compactRevision++;
        _olderPageCursor = null;
        _newerPageCursor = null;
        ClearPendingTurns();
        _isInitialLoading = false;
        _isReplacingRows = false;
        _isLoadingOlder = false;
        _isLoadingNewer = false;
        IsCompact = compact;
        NotifyLoadingState();
    }

    internal AgentCompactTranscriptRequest CreateCompactRefreshRequest(Guid sessionId, int limit)
    {
        var first = _compact?.Oldest;
        var last = _compact?.Newest;
        return first is { } start && last is { } end && !IsFollowingLatest
            ? CompactRequest(sessionId, TranscriptCompactDirections.Window, limit, start, end)
            : CompactRequest(sessionId, TranscriptCompactDirections.Recent, limit);
    }

    internal bool TryCompleteCompactInitialLoad(TranscriptLoadTicket ticket, AgentCompactTranscriptPage page)
    {
        if (!IsCompact || !IsCurrent(ticket)) return false;
        var preserve = ticket.PreserveWindow && !ticket.ReconcileAuthoritative;
        if (!preserve)
        {
            if (!ticket.PreserveWindow) _compact!.Reset();
            ApplyCompactPage(page, replace: true, older: false, _viewportAnchor?.AnchorKey);
        }
        _isInitialLoading = false;
        _isReplacingRows = false;
        _initialOperation.TryComplete(ticket.Generation);
        foreach (var pending in _pendingTurnsById.Values.Where(turn => !ticket.ReconcileAuthoritative
                     || _pendingTurnSequencesById.GetValueOrDefault(turn.TurnId) > ticket.PendingTurnSequence).ToArray())
        {
            ApplyCompactLiveTurn(pending, notifyVisualChange: true);
        }
        var needsRefresh = preserve || _pendingTurnsById.Count > 0 || _pendingTurnsOverflowed;
        ClearPendingTurns();
        _pendingTurnsOverflowed = false;
        NotifyLoadingState();
        RowsChanged?.Invoke();
        if (needsRefresh || _compactRefreshPending) RequestCompactRefresh();
        return true;
    }

    internal async Task<bool> LoadCompactAroundAsync(Guid sessionId, Guid turnId, Guid? itemId,
        CancellationToken cancellationToken, bool revealMembers = true, TranscriptModeAnchor? previousModeAnchor = null)
    {
        if (!IsCompact || _loadCompact is null) return false;
        TranscriptLoadTicket ticket = default;
        await RunOnPresentationThreadAsync(() => ticket = BeginAnchoredInitialLoad(sessionId), cancellationToken);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ticket.Generation.CancellationToken);
            var request = AroundRequest(sessionId, turnId, itemId, _initialTurnLimit);
            var page = await _loadCompact(request, linked.Token).ConfigureAwait(false);
            var applied = await RunOnPresentationThreadAsync(() =>
            {
                if (!IsCurrent(ticket)) return false;
                _compact!.Reset();
                if (previousModeAnchor is not null && page.Entries.FirstOrDefault(entry => entry.EntryId == page.AnchorEntryId) is { } entry)
                {
                    var target = entry.Group is { } group
                        ? (object)new TranscriptActivityGroupAnchor(new TranscriptCompactGroupKey(group.SessionId, group.First.ItemId))
                        : TranscriptRowAnchorKey.Text(entry.TurnId);
                    RestoreModeAnchor(previousModeAnchor, target, notify: false);
                }
                return TryCompleteCompactInitialLoad(ticket, page);
            }, linked.Token).ConfigureAwait(false);
            if (!applied || page.AnchorEntryId is not { } anchorEntryId) return false;
            await RunOnPresentationThreadAsync(() =>
            {
                _selectedAnchorKey = _compact!.AnchorForEntry(anchorEntryId);
                OnPropertyChanged(nameof(SelectedAnchorKey));
            }, linked.Token);
            if (!revealMembers) return true;
            Task reveal = Task.CompletedTask;
            await RunOnPresentationThreadAsync(() => reveal = _compact!.RevealEntryAsync(anchorEntryId, linked.Token), linked.Token);
            await reveal.ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RunOnPresentationThreadAsync(() => TryFailInitialLoad(ticket), CancellationToken.None);
            throw;
        }
    }

    internal object ResolveCompactVisibleAnchor(object anchor)
        => _modeAnchorMapping is { } mapping && Equals(mapping.Source, anchor) ? mapping.Target
            : IsCompact ? _compact?.ResolveVisibleAnchor(anchor) ?? anchor : anchor;

    internal TranscriptModeAnchor? CaptureModeAnchor()
    {
        var anchor = _viewportAnchor?.AnchorKey ?? _selectedAnchorKey
            ?? (_projector.Rows.FirstOrDefault() is { } first ? _projector.GetAnchorKey(first) : null);
        if (anchor is null) return null;
        if (IsCompact) return _compact?.CaptureModeAnchor(anchor, _viewportAnchor);
        foreach (var turn in _projector.TurnWindow.OrderedTurns())
        {
            if (Equals(TranscriptRowAnchorKey.Text(turn.TurnId), anchor))
                return new(turn.TurnId, null, AgentTranscriptTime.Read(turn.CreatedAtUtc), anchor, _viewportAnchor);
            foreach (var item in turn.Items.Where(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult))
                if (Equals(TranscriptRowAnchorKey.Tool(turn, item), anchor))
                    return new(turn.TurnId, item.ItemId, AgentTranscriptTime.Read(turn.CreatedAtUtc), anchor, _viewportAnchor);
        }
        return null;
    }

    internal void RestoreModeAnchor(TranscriptModeAnchor previous, object target, bool notify = true)
    {
        _modeAnchorMapping = (previous.AnchorKey, target);
        _selectedAnchorKey = target;
        SetViewportAnchor(previous.Viewport is { } viewport ? viewport with { AnchorKey = target }
            : new TranscriptViewportAnchorData(target, 0, 0));
        OnPropertyChanged(nameof(SelectedAnchorKey));
        if (notify) RowsChanged?.Invoke();
    }

    private bool FailCompactInitialLoad(TranscriptLoadTicket ticket)
    {
        _isInitialLoading = false;
        _isReplacingRows = false;
        ClearPendingTurns();
        _initialOperation.TryComplete(ticket.Generation, severity: OperationSeverity.Error);
        NotifyLoadingState();
        RowsChanged?.Invoke();
        return true;
    }

    private async Task<bool> LoadCompactPageAsync(bool older, object? protectedAnchorKey, CancellationToken cancellationToken)
    {
        var cursor = older ? _compact?.Oldest : _compact?.Newest;
        if ((older ? !CanLoadOlder : !CanLoadNewer) || SessionId is not { } sessionId || cursor is not { } boundary || _loadCompact is null)
            return false;
        if (older) DetachFromLatest();
        PreserveViewportAnchor(protectedAnchorKey);
        var generation = _pageOperation.Begin(older ? "Loading older transcript entries" : "Loading newer transcript entries");
        _isLoadingOlder = older;
        _isLoadingNewer = !older;
        NotifyLoadingState();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(generation.CancellationToken, cancellationToken);
            var page = await _loadCompact(CompactRequest(sessionId,
                older ? TranscriptCompactDirections.Before : TranscriptCompactDirections.After, _pageSize, boundary), linked.Token).ConfigureAwait(false);
            return await RunOnPresentationThreadAsync(() =>
            {
                if (!IsCompact || !IsPageApplicationCurrent(sessionId, linked.Token)) return false;
                var edge = older ? page.Entries.FirstOrDefault() : page.Entries.LastOrDefault();
                var hasMore = older ? page.HasOlder : page.HasNewer;
                if (hasMore && (edge is null || !(older
                    ? IsBefore(new(AgentTranscriptTime.Read(edge.CreatedAtUtc), edge.TurnId) { TurnSequence = edge.TurnSequence }, boundary)
                    : IsAfter(new(AgentTranscriptTime.Read(edge.CreatedAtUtc), edge.TurnId) { TurnSequence = edge.TurnSequence }, boundary))))
                    throw new InvalidDataException("Compact transcript pagination did not advance its cursor.");
                (protectedAnchorKey as TranscriptPageAnchorAuthority)?.PreparePublication();
                _isApplyingPageRows = true;
                try
                {
                    ApplyCompactPage(page, replace: false, older, ResolveTrimProtectedAnchorKey(protectedAnchorKey));
                }
                finally
                {
                    try { (protectedAnchorKey as TranscriptPageAnchorAuthority)?.CompletePublication(); }
                    finally { _isApplyingPageRows = false; }
                }
                return true;
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        { return false; }
        finally
        {
            await RunOnPresentationThreadAsync(() =>
            {
                if (!_pageOperation.TryComplete(generation)) return;
                _isLoadingOlder = false;
                _isLoadingNewer = false;
                NotifyLoadingState();
                if (_compactRefreshPending) RequestCompactRefresh();
            }, CancellationToken.None);
        }
    }

    private void ApplyCompactPage(AgentCompactTranscriptPage page, bool replace, bool older, object? protectedAnchor)
    {
        if (SessionId is not { } session) return;
        TranscriptCompactValidation.Page(page, session);
        using ((_projector.Rows as TranscriptObservableCollection<TRow>)?.DeferReconciliation())
        {
            var trim = _compact!.Apply(page, replace, older, _visibleRowLimit, protectedAnchor);
            _compactRevision++;
            if (replace || older) SetHasOlderRows(page.HasOlder);
            if (replace || !older) SetHasNewerRows(page.HasNewer);
            if (trim.HasFlag(TranscriptTrimResult.Oldest)) SetHasOlderRows(true);
            if (trim.HasFlag(TranscriptTrimResult.Newest)) SetHasNewerRows(true);
        }
        PruneExpandedAnchorKeys();
    }

    private TranscriptLiveTurnResult ApplyCompactLiveTurn(AgentTurnRecord turn, bool notifyVisualChange, bool hasGroupSummary = false)
    {
        turn = TranscriptTurnTransportProjection.ProjectToolHeaders(turn);
        _compactRevision++;
        var applied = _compact!.ApplyLiveTurn(turn);
        if (!applied && HasOlderRows && turn.Kind == AgentTurnKind.Message
            && _compact.Oldest is { } oldest && IsBefore(TranscriptPageCursor.FromTurn(turn), oldest))
            return TranscriptLiveTurnResult.OutsideWindow;
        if (!applied && IsFollowingLatest && _compact.TryAppendLiveMessage(turn, _visibleRowLimit, out var trimmed))
        {
            applied = true;
            if (trimmed) SetHasOlderRows(true);
        }
        else if (!applied && !IsFollowingLatest && turn.Kind == AgentTurnKind.Message
                 && _compact.Newest is { } newest && IsAfter(TranscriptPageCursor.FromTurn(turn), newest))
            SetHasNewerRows(true);
        TurnProjected?.Invoke(turn, notifyVisualChange || turn.IsStreaming, IsFollowingLatest);
        if (!hasGroupSummary && turn.Kind is AgentTurnKind.ToolCall or AgentTurnKind.ToolResult) RequestCompactRefresh();
        return applied ? TranscriptLiveTurnResult.Applied : TranscriptLiveTurnResult.Buffered;
    }

    private void RequestCompactRefresh()
    {
        _compactRefreshPending = true;
        if (IsCompact && !IsInitialLoading && !IsLoadingOlder && !IsLoadingNewer) _compactRefresh?.MarkDirty();
    }

    private async Task RefreshCompactWindowAsync(CancellationToken cancellationToken)
    {
        AgentCompactTranscriptRequest? request = null;
        long revision = 0;
        await RunOnPresentationThreadAsync(() =>
        {
            if (!IsCompact || IsInitialLoading || IsLoadingOlder || IsLoadingNewer || SessionId is not { } session) return;
            _compactRefreshPending = false;
            revision = _compactRevision;
            request = CreateCompactRefreshRequest(session, Math.Max(_initialTurnLimit, _compact!.Entries.Count));
        }, cancellationToken);
        if (request is null || _loadCompact is null) return;
        var page = await _loadCompact(request, cancellationToken).ConfigureAwait(false);
        await RunOnPresentationThreadAsync(() =>
        {
            if (!IsCompact || SessionId != request.SessionId || IsInitialLoading) return;
            if (revision != _compactRevision || IsLoadingOlder || IsLoadingNewer) { RequestCompactRefresh(); return; }
            if (_compact!.Matches(page))
            {
                SetHasOlderRows(page.HasOlder);
                SetHasNewerRows(page.HasNewer);
                return;
            }
            ApplyCompactPage(page, replace: true, older: false, IsFollowingLatest ? null : _viewportAnchor?.AnchorKey);
        }, cancellationToken);
    }

    private void OnCompactRowsChanged() => RowsChanged?.Invoke();

    private static AgentCompactTranscriptRequest CompactRequest(Guid sessionId, AgentCompactTranscriptDirection direction,
        int limit, TranscriptPageCursor? anchor = null, TranscriptPageCursor? end = null)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { SessionId = sessionId, Direction = direction, Limit = limit,
            AnchorCreatedAtUtc = anchor is { } a ? AgentTranscriptTime.Write(a.CreatedAtUtc) : null, AnchorTurnId = anchor?.TurnId,
            EndCreatedAtUtc = end is { } e ? AgentTranscriptTime.Write(e.CreatedAtUtc) : null, EndTurnId = end?.TurnId };
#else
        => new(sessionId, direction, limit, anchor?.CreatedAtUtc, anchor?.TurnId, EndCreatedAtUtc: end?.CreatedAtUtc, EndTurnId: end?.TurnId);
#endif

    private static AgentCompactTranscriptRequest AroundRequest(Guid sessionId, Guid turnId, Guid? itemId, int limit)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { SessionId = sessionId, Direction = TranscriptCompactDirections.Around, Limit = limit, AnchorTurnId = turnId, AnchorItemId = itemId };
#else
        => new(sessionId, TranscriptCompactDirections.Around, limit, AnchorTurnId: turnId, AnchorItemId: itemId);
#endif
}

internal sealed record TranscriptModeAnchor(Guid TurnId, Guid? ItemId, DateTimeOffset CreatedAtUtc,
    object AnchorKey, TranscriptViewportAnchorData? Viewport);

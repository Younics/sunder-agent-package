using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private void RefreshTranscript(bool forceReplacement = false)
    {
        var displayedSession = DisplayedSession;
        if (displayedSession is null)
        {
            _runActivity.Reset();
            _timeline.ClearSession();
            StatusText = string.IsNullOrWhiteSpace(_globalStatusText)
                ? GetSetupStatusText()
                : _globalStatusText;
            return;
        }

        if (_timeline.SessionId != displayedSession.SessionId)
        {
            _runActivity.Reset();
        }
        var previousRunActivityTurns = _timeline.SessionId == displayedSession.SessionId
            ? SelectRunActivityTurns(_timeline.Projector.TurnWindow.OrderedTurns())
            : null;
        var ticket = _timeline.BeginInitialLoad(
            displayedSession.SessionId,
            forceReplacement);
        StatusText = "Loading transcript...";
        _backgroundTasks.Run(_ => RefreshTranscriptAsync(
            displayedSession,
            ticket,
            previousRunActivityTurns));
    }

    private async Task RefreshTranscriptAsync(
        AgentSessionListItemViewModel displayedSession,
        TranscriptLoadTicket ticket,
        IReadOnlyList<AgentTurnRecord>? previousRunActivityTurns)
    {
        try
        {
            ticket.Generation.CancellationToken.ThrowIfCancellationRequested();
            var page = await LoadTranscriptPageAsync(
                new AgentTranscriptPageRequest(
                    displayedSession.SessionId,
                    AgentTranscriptPageDirection.Recent,
                    InitialTranscriptTurnLimit),
                ticket.Generation.CancellationToken).ConfigureAwait(false);
            await InvokeOnUiThreadAsync(
                () => CompleteTranscriptRefresh(
                    displayedSession,
                    ticket,
                    page,
                    previousRunActivityTurns));
        }
        catch (OperationCanceledException) when (ticket.Generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (_timeline.TryFailInitialLoad(ticket))
                {
                    StatusText = $"Unable to load transcript: {ex.Message}";
                }
            });
        }
    }

    private void CompleteTranscriptRefresh(
        AgentSessionListItemViewModel displayedSession,
        TranscriptLoadTicket ticket,
        AgentTranscriptPage page,
        IReadOnlyList<AgentTurnRecord>? previousRunActivityTurns)
    {
        if (!_timeline.TryCompleteInitialLoad(
                ticket,
                page.Turns,
                page.HasMore,
                page.Continuation))
        {
            return;
        }

        if (previousRunActivityTurns is not null)
        {
            ReconcileRunActivityAfterAuthoritativeReplacement(previousRunActivityTurns);
        }
        TrackCheckpointActivity(_sessionService.GetLatestCheckpoint(displayedSession.SessionId));
        ApplyRunActivityState();
        UpdateSessionState(displayedSession.SessionId, markUnread: false);
        displayedSession.ClearUnreadActivity();
        StatusText = displayedSession.StatusText;
    }

    public async Task<bool> LoadOlderTranscriptRowsAsync(
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = await _timeline.LoadOlderAsync(
            async (sessionId, beforeCreatedAt, beforeTurnId, limit, pageCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pageCancellationToken);
                var page = await LoadTranscriptPageAsync(
                    new AgentTranscriptPageRequest(
                        sessionId,
                        AgentTranscriptPageDirection.Before,
                        limit,
                        beforeCreatedAt,
                        beforeTurnId),
                    linkedCancellation.Token).ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return new TranscriptTurnPage(page.Turns, page.HasMore, page.Continuation);
            },
            protectedAnchorKey,
            cancellationToken).ConfigureAwait(false);
        if (loaded)
        {
            await InvokeOnUiThreadAsync(ApplyRunActivityState).ConfigureAwait(false);
        }

        return loaded;
    }

    public async Task<bool> LoadNewerTranscriptRowsAsync(
        object? protectedAnchorKey = null,
        CancellationToken cancellationToken = default,
        bool resumeFollowingWhenCaughtUp = true)
    {
        var loaded = await _timeline.LoadNewerAsync(
            async (sessionId, afterCreatedAt, afterTurnId, limit, pageCancellationToken) =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pageCancellationToken);
                var page = await LoadTranscriptPageAsync(
                    new AgentTranscriptPageRequest(
                        sessionId,
                        AgentTranscriptPageDirection.After,
                        limit,
                        afterCreatedAt,
                        afterTurnId),
                    linkedCancellation.Token).ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                return new TranscriptTurnPage(page.Turns, page.HasMore, page.Continuation);
            },
            protectedAnchorKey,
            cancellationToken).ConfigureAwait(false);
        if (loaded)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (resumeFollowingWhenCaughtUp)
                {
                    _timeline.ResumeFollowingLatestIfCaughtUp();
                }
                _runActivity.NotifyFollowStateChanged();
            }).ConfigureAwait(false);
        }

        return loaded;
    }

    internal void ReportTranscriptPagingFailure(Exception exception)
        => RunOnUiThread(() =>
        {
            if (!_disposed)
            {
                StatusText = $"Unable to load transcript: {exception.Message}";
            }
        });

    [RelayCommand]
    private void JumpToLatestTranscript()
        => RequestTranscriptTailFollow(DisplayedTranscriptSessionId);

    private void RequestTranscriptTailFollow(Guid? sessionId)
    {
        if (sessionId is not { } targetSessionId
            || DisplayedTranscriptSessionId != targetSessionId
            || !_timeline.RequestJumpToLatest())
        {
            return;
        }

        _runActivity.NotifyFollowStateChanged();
        TranscriptTailFollowRequested?.Invoke(targetSessionId);
        if (_timeline.HasNewerRows)
        {
            RefreshTranscript();
        }
    }

    public bool DetachTranscriptFromLatest()
    {
        if (_timeline.DetachFromLatest())
        {
            _runActivity.NotifyFollowStateChanged();
            return true;
        }

        return false;
    }

    public bool ResumeTranscriptFollowingLatestIfCaughtUp()
    {
        if (_timeline.ResumeFollowingLatestIfCaughtUp())
        {
            _runActivity.NotifyFollowStateChanged();
            return true;
        }

        return false;
    }

    internal void SetTranscriptJumpToLatestVisible(bool isVisible)
        => _timeline.SetJumpToLatestVisible(isVisible);

    internal void SetTranscriptViewportAnchor(TranscriptViewportAnchorData? anchor)
        => _timeline.SetViewportAnchor(anchor);

    internal TranscriptViewportAnchorData? TranscriptViewportAnchor
        => _timeline.ViewportAnchor;

    internal bool IsTranscriptFollowingLatest => _timeline.IsFollowingLatest;

    internal void SetTranscriptPresentationActive(bool isActive)
        => _activityTicker.SetEnabled(isActive);

    internal void SetTranscriptRowExpanded(AgentTranscriptRowViewModel row, bool isExpanded)
        => _timeline.SetRowExpanded(row, isExpanded);

    private void OnTimelineRowsChanging(bool isPageApplication)
    {
        if (!_isBatchingTranscriptNotifications)
        {
            TranscriptChanging?.Invoke(isPageApplication);
            return;
        }

        if (_transcriptChangingRaisedInBatch)
        {
            return;
        }

        _transcriptChangingRaisedInBatch = true;
        TranscriptChanging?.Invoke(isPageApplication);
    }

    private void OnTimelineRowsChanged()
    {
        if (_isBatchingTranscriptNotifications)
        {
            _transcriptChangedInBatch = true;
            return;
        }

        TranscriptChanged?.Invoke();
    }

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
    {
        if (!_isInitialized)
        {
            return;
        }

        EnqueueTranscriptBoundary(() =>
        {
            if (DisplayedSession?.SessionId == sessionId)
            {
                PrepareForSubmittedUserTurn(turn);
                _timeline.ApplyLiveTurn(turn);
            }
        });
    }

    private void OnTurnMutated(AgentTurnMutation mutation)
    {
        if (!_isInitialized)
        {
            return;
        }

        var scheduleDrain = false;
        lock (_turnMutationQueueLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_openTurnMutationBatch is null)
            {
                _openTurnMutationBatch = [];
                var batch = _openTurnMutationBatch;
                _transcriptWorkQueue.Enqueue(() => DrainTurnMutations(batch));
            }
            _openTurnMutationBatch.Add(mutation);
            scheduleDrain = MarkTranscriptWorkDrainScheduled();
        }

        if (scheduleDrain)
        {
            ScheduleTranscriptWorkDrain();
        }
    }

    private void EnqueueTranscriptBoundary(Action action)
    {
        var scheduleDrain = false;
        lock (_turnMutationQueueLock)
        {
            if (_disposed)
            {
                return;
            }

            _openTurnMutationBatch = null;
            _transcriptWorkQueue.Enqueue(action);
            scheduleDrain = MarkTranscriptWorkDrainScheduled();
        }

        if (scheduleDrain)
        {
            ScheduleTranscriptWorkDrain();
        }
    }

    private Task<T> EnqueueTranscriptBoundaryAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduleDrain = false;
        lock (_turnMutationQueueLock)
        {
            if (_disposed)
            {
                return Task.FromCanceled<T>(new CancellationToken(canceled: true));
            }

            _openTurnMutationBatch = null;
            _transcriptWorkQueue.Enqueue(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    throw;
                }
            });
            scheduleDrain = MarkTranscriptWorkDrainScheduled();
        }

        if (scheduleDrain)
        {
            ScheduleTranscriptWorkDrain();
        }

        return completion.Task.WaitAsync(_lifetimeCancellation.Token);
    }

    private bool MarkTranscriptWorkDrainScheduled()
    {
        if (_transcriptWorkDrainScheduled)
        {
            return false;
        }

        _transcriptWorkDrainScheduled = true;
        return true;
    }

    private void ScheduleTranscriptWorkDrain()
        => _backgroundTasks.Run(_ => InvokeOnUiThreadAsync(DrainTranscriptWorkQueue));

    private void DrainTranscriptWorkQueue()
    {
        while (true)
        {
            Action work;
            lock (_turnMutationQueueLock)
            {
                if (_disposed)
                {
                    _openTurnMutationBatch = null;
                    _transcriptWorkQueue.Clear();
                    _transcriptWorkDrainScheduled = false;
                    return;
                }
                if (!_transcriptWorkQueue.TryDequeue(out var queuedWork))
                {
                    _transcriptWorkDrainScheduled = false;
                    return;
                }
                work = queuedWork;
            }

            try
            {
                work();
            }
            catch (Exception exception)
            {
                ReportPresentationFailure(exception);
            }
        }
    }

    private void DrainTurnMutations(List<AgentTurnMutation> batch)
    {
        AgentTurnMutation[] mutations;
        lock (_turnMutationQueueLock)
        {
            if (ReferenceEquals(_openTurnMutationBatch, batch))
            {
                _openTurnMutationBatch = null;
            }
            if (_disposed)
            {
                batch.Clear();
                return;
            }

            mutations = batch.ToArray();
            batch.Clear();
        }

        RunTranscriptNotificationBatch(() =>
        {
            foreach (var mutation in mutations)
            {
                try
                {
                    if (mutation.Turn is { } submittedTurn)
                    {
                        PrepareForSubmittedUserTurn(submittedTurn);
                    }
                    var result = _timeline.ApplyLiveMutation(mutation);
                    if (mutation.Turn is { } turn
                        && DisplayedTranscriptSessionId != turn.SessionId)
                    {
                        CommitSubmittedUserTurn(turn);
                    }
                    if (result == TranscriptLiveTurnResult.ReloadRequired
                        && DisplayedTranscriptSessionId == mutation.SessionId)
                    {
                        RefreshTranscript();
                    }
                }
                catch (Exception exception)
                {
                    ReportPresentationFailure(exception);
                }
            }
        });
    }

    private void RunTranscriptNotificationBatch(Action action)
    {
        BeginTranscriptNotificationBatch();
        try
        {
            action();
        }
        finally
        {
            try
            {
                EndTranscriptNotificationBatch();
            }
            finally
            {
                _isBatchingTranscriptNotifications = false;
                _transcriptChangingRaisedInBatch = false;
                _transcriptChangedInBatch = false;
            }
        }
    }

    private void BeginTranscriptNotificationBatch()
    {
        _isBatchingTranscriptNotifications = true;
        _transcriptChangingRaisedInBatch = false;
        _transcriptChangedInBatch = false;
    }

    private void EndTranscriptNotificationBatch()
    {
        var raiseChanged = _transcriptChangedInBatch;
        _isBatchingTranscriptNotifications = false;
        _transcriptChangingRaisedInBatch = false;
        _transcriptChangedInBatch = false;
        if (raiseChanged)
        {
            TranscriptChanged?.Invoke();
        }
    }

    private void OnTranscriptReset(Guid sessionId)
    {
        if (!_isInitialized)
        {
            return;
        }

        EnqueueTranscriptBoundary(() =>
        {
            if (DisplayedSession?.SessionId == sessionId)
            {
                RefreshTranscript(forceReplacement: true);
            }
        });
    }

    private void OnRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
    {
        if (_isInitialized)
        {
            EnqueueTranscriptBoundary(() => ApplyRunActivityChanged(sessionId, activity));
        }
    }

    private void ApplyRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
    {
        if (DisplayedSession?.SessionId != sessionId
            || !IsDisplayedSessionRunActive
            || !_timeline.IsFollowingLatest)
        {
            return;
        }

        var checkpoint = _sessionService.GetLatestCheckpoint(sessionId);
        if (checkpoint?.Status != AgentRunStatus.Running
            || checkpoint.RunRevision != activity.RunRevision)
        {
            return;
        }

        _runActivity.TrackUpdate(
            activity.Text,
            activity.Kind == AgentRunActivityKind.Reasoning);
    }

    private void OnTimelineTurnProjected(
        AgentTurnRecord turn,
        bool trackRunActivity,
        bool scheduleQuietTimer)
    {
        var committedSubmission = CommitSubmittedUserTurn(turn);
        if (trackRunActivity && !committedSubmission)
        {
            _runActivity.TrackTurn(turn, scheduleQuietTimer);
        }
    }

    private void OnRunActivityStateChanged()
        => ApplyRunActivityState();

    private void ApplyRunActivityState()
        => RunActivityRow.SetPresentation(
            _runActivity.Text,
            _runActivity.IsReasoning,
            _runActivity.ShouldShow);

    private void UpdateActivityRowForCurrentState() => ApplyRunActivityState();

    private void TrackCheckpointActivity(AgentRunCheckpointRecord? checkpoint)
        => _runActivity.TrackCheckpoint(checkpoint);

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.HasOlderRows):
                OnPropertyChanged(nameof(HasOlderTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.HasNewerRows):
                OnPropertyChanged(nameof(HasNewerTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.IsLoadingOlder):
                OnPropertyChanged(nameof(IsLoadingOlderTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.IsLoadingNewer):
                OnPropertyChanged(nameof(IsLoadingNewerTranscriptRows));
                break;
            case nameof(TranscriptTimelineState<AgentTranscriptRowViewModel>.IsInitialLoading):
                OnPropertyChanged(nameof(IsTranscriptLoading));
                break;
        }

        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));
    }

    private void RefreshVisibleChildSessionLinks()
        => _timeline.RefreshRelatedRows();

    private IReadOnlyList<AgentChildSessionLinkViewModel> ResolveChildSessionLinksFromStore(
        AgentTurnRecord turn,
        AgentTurnItemRecord item)
    {
        if (!IsSubagentTool(item.ToolId) || string.IsNullOrWhiteSpace(item.CallId))
        {
            return [];
        }

        var parentSession = _sessionService.GetSession(turn.SessionId)
                            ?? _snapshotSessions.GetValueOrDefault(turn.SessionId)?.Session;
        if (parentSession is null)
        {
            return [];
        }

        var workspaceSessions = _sessionService.ListSessionsForWorkspace(
            parentSession.WorkspaceId ?? string.Empty);
        if (workspaceSessions.Count == 0)
        {
            workspaceSessions = _snapshotSessions.Values
                .Select(static item => item.Session)
                .Where(session => string.Equals(
                    session.WorkspaceId,
                    parentSession.WorkspaceId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return workspaceSessions
            .Where(session => session.ParentSessionId == parentSession.SessionId
                              && string.Equals(
                                  session.ParentToolCallId,
                                  item.CallId,
                                  StringComparison.Ordinal))
            .OrderBy(session => session.CreatedAtUtc)
            .Select(CreateChildSessionLink)
            .ToArray();
    }

    private AgentChildSessionLinkViewModel CreateChildSessionLink(AgentSessionRecord childSession)
    {
        var childProfile = string.IsNullOrWhiteSpace(childSession.ProfileId)
            ? null
            : Profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                childSession.ProfileId,
                StringComparison.OrdinalIgnoreCase));
        return new AgentChildSessionLinkViewModel(
            childSession.SessionId,
            childSession.Title,
            FormatChildSessionSubtitle(childProfile?.DisplayName, childSession.AgentKind),
            (_sessionService.GetLatestCheckpoint(childSession.SessionId)
             ?? _snapshotSessions.GetValueOrDefault(childSession.SessionId)?.Checkpoint)?.Status
            ?? AgentRunStatus.Idle);
    }

    private static bool IsSubagentTool(string? toolId)
        => string.Equals(toolId, "task", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolId, "delegate_tasks", StringComparison.OrdinalIgnoreCase);

    private static string FormatChildSessionSubtitle(
        string? profileDisplayName,
        string? agentKind)
    {
        var name = string.IsNullOrWhiteSpace(profileDisplayName)
            ? agentKind
            : profileDisplayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Subagent";
        }

        return string.Equals(agentKind, "subagent", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? $"{name} subagent"
            : name;
    }

    private string ResolveTurnSenderDisplayName(AgentTurnRecord turn)
    {
        var session = _sessionService.GetSession(turn.SessionId)
                      ?? _snapshotSessions.GetValueOrDefault(turn.SessionId)?.Session;
        var profileId = session?.ProfileId;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var profile = Profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(profile?.DisplayName))
            {
                return profile.DisplayName.Trim();
            }
        }

        var hasSpecificAgentKind = !string.IsNullOrWhiteSpace(session?.AgentKind)
            && !string.Equals(session.AgentKind, "agent", StringComparison.OrdinalIgnoreCase);
        if (SelectedProfile is not null
            && !hasSpecificAgentKind
            && (session is null
                || string.IsNullOrWhiteSpace(session.ProfileId)
                || string.Equals(
                    session.ProfileId,
                    SelectedProfile.ProfileId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return SelectedProfile.DisplayName;
        }

        return !string.IsNullOrWhiteSpace(session?.AgentKind)
            ? session.AgentKind.Trim()
            : "Agent";
    }

    private Task<AgentTranscriptPage> LoadTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken)
    {
        if (_transcriptPageGateway is not null)
        {
            return _transcriptPageGateway.LoadTranscriptPageAsync(request, cancellationToken);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Clamp(request.Limit, 1, 500);
            var transcriptHeaders = _sessionService as IAgentTranscriptHeaderGateway;
            IReadOnlyList<AgentTurnRecord> turns = request.Direction switch
            {
                AgentTranscriptPageDirection.Recent =>
                    transcriptHeaders?.ListRecentTranscriptHeaders(request.SessionId, limit + 1)
                    ?? _sessionService.ListRecentTurns(request.SessionId, limit + 1)
                        .Select(TranscriptTurnTransportProjection.ProjectToolHeaders)
                        .ToArray(),
                AgentTranscriptPageDirection.Before when request.AnchorCreatedAtUtc is { } createdAt
                                                          && request.AnchorTurnId is { } turnId =>
                    transcriptHeaders?.ListTranscriptHeadersBefore(
                        request.SessionId,
                        createdAt,
                        turnId,
                        limit + 1)
                    ?? _sessionService.ListTurnsBefore(request.SessionId, createdAt, turnId, limit + 1)
                        .Select(TranscriptTurnTransportProjection.ProjectToolHeaders)
                        .ToArray(),
                AgentTranscriptPageDirection.After when request.AnchorCreatedAtUtc is { } createdAt
                                                         && request.AnchorTurnId is { } turnId =>
                    transcriptHeaders?.ListTranscriptHeadersAfter(
                        request.SessionId,
                        createdAt,
                        turnId,
                        limit + 1)
                    ?? _sessionService.ListTurnsAfter(request.SessionId, createdAt, turnId, limit + 1)
                        .Select(TranscriptTurnTransportProjection.ProjectToolHeaders)
                        .ToArray(),
                AgentTranscriptPageDirection.Turn when request.AnchorTurnId is { } turnId =>
                    (transcriptHeaders is null
                        ? _sessionService.GetTurn(turnId)
                        : transcriptHeaders.GetTranscriptHeader(turnId)) is { } turn
                        ? [TranscriptTurnTransportProjection.ProjectToolHeaders(turn)]
                        : [],
                _ => throw new InvalidOperationException("The transcript page anchor is invalid."),
            };
            cancellationToken.ThrowIfCancellationRequested();
            var hasMore = turns.Count > limit;
            var pageTurns = !hasMore
                ? turns
                : request.Direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before
                    ? turns.Skip(turns.Count - limit).ToArray()
                    : turns.Take(limit).ToArray();
            return new AgentTranscriptPage(
                0,
                pageTurns,
                hasMore);
        }, cancellationToken);
    }
}

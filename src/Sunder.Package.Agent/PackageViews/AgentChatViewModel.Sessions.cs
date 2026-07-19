using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    partial void OnSelectedSessionChanging(
        AgentSessionListItemViewModel? oldValue,
        AgentSessionListItemViewModel? newValue
    )
    {
        if (_isApplyingChatSnapshot)
        {
            return;
        }

        if (_isReconcilingSessionSelection)
        {
            return;
        }

        if (oldValue is not null)
        {
            if (IsRollbackPending)
            {
                ClearRollbackStateOnly();
                DraftMessage = string.Empty;
            }

            oldValue.DraftMessage = DraftMessage;
            oldValue.IsSelected = false;
        }

        if (!_isRestoringReconciledSessionSelection && oldValue?.SessionId != newValue?.SessionId)
        {
            ClearPendingAttachments();
        }

        if (_observedSelectedSession is not null)
        {
            _observedSelectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
            _observedSelectedSession = null;
        }
    }

    partial void OnSelectedSessionChanged(AgentSessionListItemViewModel? value)
    {
        if (_isApplyingChatSnapshot)
        {
            return;
        }

        if (_isReconcilingSessionSelection)
        {
            return;
        }

        if (value is not null)
        {
            value.IsSelected = true;
            value.ClearUnreadActivity();
            _observedSelectedSession = value;
            _observedSelectedSession.PropertyChanged += OnSelectedSessionPropertyChanged;
            DraftMessage = value.DraftMessage;
        }
        else
        {
            DraftMessage = string.Empty;
        }

        NotifySelectedSessionRunStateChanged();
        RefreshSetupState();
        ScheduleChatSnapshotRequest(
            SelectedProfile?.ProfileId,
            SelectedWorkspace?.WorkspaceId,
            value?.SessionId);
    }

    partial void OnDisplayedSessionChanged(AgentSessionListItemViewModel? value)
    {
        if (_isApplyingChatSnapshot)
        {
            return;
        }

        value?.ClearUnreadActivity();
        NotifyDisplayedSessionStateChanged();
        RefreshSetupState();
        RefreshTranscript();
    }

    [RelayCommand]
    private async Task OpenChildSessionAsync(AgentChildSessionLinkViewModel? childSession)
    {
        if (childSession is null)
        {
            return;
        }

        var session = _sessionService.GetSession(childSession.SessionId);
        if (session is null)
        {
            SetGlobalStatus("The selected sub-session is no longer available.");
            return;
        }

        if (_shellViewService is null)
        {
            SetGlobalStatus("The Subsessions view is unavailable in this host.");
            return;
        }

        var opened = await _shellViewService.OpenViewPanelAsync(
            SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubsessionNavigationSessionIdKey] = childSession.SessionId.ToString("D"),
            }
        );
        if (!opened)
        {
            SetGlobalStatus("Unable to open the Subsessions view.");
        }
    }

    private void ReloadSessions(Guid? selectSessionId)
    {
        if (SelectedWorkspace is null)
        {
            ReconcileSessions([]);
            SelectedSession = null;
            SetDisplayedSession(null);
            TrackBackgroundTask(_selectionState?.SaveSelectedSessionIdAsync((string?)null, null));
            RefreshSetupState();
            return;
        }

        ReconcileSessions(ListMainSessions());
        if (selectSessionId is null && SelectedSession is not null)
        {
            RefreshSetupState();
            return;
        }

        var nextSession =
            ResolveSessionItem(selectSessionId, allowChildSession: false)
            ?? Sessions.FirstOrDefault(session => session.SessionId == SelectedSession?.SessionId)
            ?? Sessions.FirstOrDefault();
        SelectedSession = nextSession;
        if (nextSession is null)
        {
            SetDisplayedSession(null);
            TrackBackgroundTask(_selectionState?.SaveSelectedSessionIdAsync(SelectedWorkspace?.WorkspaceId, null));
            RefreshSetupState();
        }
    }

    private void SetDisplayedSession(AgentSessionListItemViewModel? session)
    {
        if (DisplayedSession?.SessionId == session?.SessionId)
        {
            if (session is not null && !ReferenceEquals(DisplayedSession, session))
            {
                DisplayedSession = session;
            }

            return;
        }

        DisplayedSession = session;
    }

    private IReadOnlyList<AgentSessionRecord> ListMainSessions() =>
        SelectedWorkspace is null
            ? []
            : _sessionService.ListSessionsForWorkspace(SelectedWorkspace.WorkspaceId)
                .Where(session => session.ParentSessionId is null)
                .ToArray();

    private AgentSessionListItemViewModel? ResolveSessionItem(
        Guid? sessionId,
        bool allowChildSession
    )
    {
        if (sessionId is null)
        {
            return null;
        }

        var existing = Sessions.FirstOrDefault(session => session.SessionId == sessionId.Value);
        if (existing is not null)
        {
            return existing;
        }

        var session = _sessionService.GetSession(sessionId.Value);
        if (session is null || !IsSessionInSelectedWorkspace(session))
        {
            return null;
        }

        if (!allowChildSession && session.ParentSessionId is not null)
        {
            var rootSessionId = session.RootSessionId ?? session.ParentSessionId.Value;
            return Sessions.FirstOrDefault(candidate => candidate.SessionId == rootSessionId)
                ?? (
                    _sessionService.GetSession(rootSessionId)
                        is { ParentSessionId: null } rootSession
                        && IsSessionInSelectedWorkspace(rootSession)
                        ? CreateSessionItem(rootSession)
                        : null
                );
        }

        return CreateSessionItem(session);
    }

    private void ReloadPendingPermissionRequests()
    {
        var sessionId = SelectedSession?.SessionId;
        if (_chatPermissionCommandGateway is not null && sessionId is { } selectedSessionId)
        {
            var generation = Interlocked.Increment(ref _permissionRequestGeneration);
            _backgroundTasks.Run(async cancellationToken =>
            {
                var projection = await _chatPermissionCommandGateway
                    .LoadSessionPermissionsAsync(selectedSessionId, cancellationToken)
                    .ConfigureAwait(false);
                await InvokeOnUiThreadAsync(() =>
                {
                    if (_disposed
                        || generation != Volatile.Read(ref _permissionRequestGeneration)
                        || SelectedSession?.SessionId != selectedSessionId
                        || projection.Revision < _appliedPermissionRevision)
                    {
                        return;
                    }

                    _appliedPermissionRevision = projection.Revision;
                    _permissionPanel.ApplySnapshot(
                        selectedSessionId,
                        projection.SessionState,
                        projection.PendingRequests);
                    NotifyPermissionPanelChanged();
                }).ConfigureAwait(false);
            });
            return;
        }

        if (sessionId is null)
        {
            Interlocked.Increment(ref _permissionRequestGeneration);
            _appliedPermissionRevision = 0;
            _permissionPanel.ApplySnapshot(null, null, []);
            NotifyPermissionPanelChanged();
            return;
        }

        _permissionPanel.Reload();
        NotifyPermissionPanelChanged();
    }

    private void NotifyPermissionPanelChanged()
    {
        OnPropertyChanged(nameof(PendingPermissionRequests));
        OnPropertyChanged(nameof(HasPendingPermissionRequests));
    }

    private void ReconcileSessions(IReadOnlyList<AgentSessionRecord> sessions)
    {
        var desiredSessionIds = sessions.Select(session => session.SessionId).ToHashSet();
        var selectedSessionId = SelectedSession?.SessionId;
        var displayedSessionId = DisplayedSession?.SessionId;
        var shouldPreserveSelectedSession =
            selectedSessionId is not null && desiredSessionIds.Contains(selectedSessionId.Value);
        var shouldPreserveDisplayedSession =
            displayedSessionId is not null && desiredSessionIds.Contains(displayedSessionId.Value);

        _isReconcilingSessionSelection = shouldPreserveSelectedSession;
        try
        {
            for (var index = Sessions.Count - 1; index >= 0; index--)
            {
                if (desiredSessionIds.Contains(Sessions[index].SessionId))
                {
                    continue;
                }

                Sessions.RemoveAt(index);
            }

            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                var existingIndex = FindSessionIndex(session.SessionId);
                if (existingIndex < 0)
                {
                    Sessions.Insert(index, CreateSessionItem(session));
                    continue;
                }

                Sessions[existingIndex].UpdateSession(session);
                Sessions[existingIndex]
                    .ApplyCheckpoint(
                        _sessionService.GetLatestCheckpoint(session.SessionId),
                        markUnread: false
                    );

                if (existingIndex == index)
                {
                    continue;
                }

                Sessions.Move(existingIndex, index);
            }
        }
        finally
        {
            _isReconcilingSessionSelection = false;
        }

        if (selectedSessionId is not null && !desiredSessionIds.Contains(selectedSessionId.Value))
        {
            SelectedSession = Sessions.FirstOrDefault();
            return;
        }

        RestoreReconciledSessionSelection(selectedSessionId, shouldPreserveSelectedSession);
        RestoreReconciledDisplayedSession(displayedSessionId, shouldPreserveDisplayedSession);
    }

    private void ReconcileSessionSnapshots(IReadOnlyList<AgentSessionSnapshot> snapshots)
    {
        var rootSnapshots = snapshots
            .Where(static item => item.Session.ParentSessionId is null)
            .ToArray();
        var desiredSessionIds = rootSnapshots
            .Select(static item => item.Session.SessionId)
            .ToHashSet();
        foreach (var item in Sessions)
        {
            item.IsSelected = false;
        }
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!desiredSessionIds.Contains(Sessions[index].SessionId))
            {
                Sessions.RemoveAt(index);
            }
        }
        for (var index = 0; index < rootSnapshots.Length; index++)
        {
            var snapshot = rootSnapshots[index];
            var existingIndex = FindSessionIndex(snapshot.Session.SessionId);
            if (existingIndex < 0)
            {
                Sessions.Insert(index, CreateSessionItem(snapshot.Session, snapshot.Checkpoint));
                continue;
            }

            Sessions[existingIndex].UpdateSession(snapshot.Session);
            Sessions[existingIndex].ApplyCheckpoint(snapshot.Checkpoint, markUnread: false);
            if (existingIndex != index)
            {
                Sessions.Move(existingIndex, index);
            }
        }
    }

    private void RestoreReconciledSessionSelection(Guid? sessionId, bool shouldPreserveSession)
    {
        if (
            !shouldPreserveSession
            || sessionId is null
            || SelectedSession?.SessionId == sessionId.Value
        )
        {
            return;
        }

        var session = FindSessionItem(sessionId.Value);
        if (session is null)
        {
            return;
        }

        _isRestoringReconciledSessionSelection = true;
        try
        {
            SelectedSession = session;
        }
        finally
        {
            _isRestoringReconciledSessionSelection = false;
        }
    }

    private void RestoreReconciledDisplayedSession(Guid? sessionId, bool shouldPreserveSession)
    {
        if (
            !shouldPreserveSession
            || sessionId is null
            || DisplayedSession?.SessionId == sessionId.Value
        )
        {
            return;
        }

        if (FindSessionItem(sessionId.Value) is { } session)
        {
            SetDisplayedSession(session);
        }
    }

    private int FindSessionIndex(Guid sessionId)
    {
        for (var index = 0; index < Sessions.Count; index++)
        {
            if (Sessions[index].SessionId == sessionId)
            {
                return index;
            }
        }

        return -1;
    }

    private AgentSessionListItemViewModel? FindSessionItem(Guid sessionId)
    {
        var index = FindSessionIndex(sessionId);
        return index < 0 ? null : Sessions[index];
    }

    private void OnSessionChanged(Guid sessionId)
    {
        if (_isInitialized)
        {
            EnqueueTranscriptBoundary(() => ApplySessionChanged(sessionId));
        }
    }

    private void ApplySessionChanged(Guid sessionId)
    {
        var selectedSessionId = SelectedSession?.SessionId;
        var displayedSessionId = DisplayedSession?.SessionId;
        var changedSession = _sessionService.GetSession(sessionId);
        if (changedSession is null)
        {
            _snapshotSessions.Remove(sessionId);
            _sessionDrafts.Remove(sessionId);
            RemoveMainSession(sessionId);
            return;
        }
        _snapshotSessions[sessionId] = new AgentSessionSnapshot(
            changedSession,
            _sessionService.GetLatestCheckpoint(sessionId));

        if (changedSession.ParentSessionId is null)
        {
            if (IsSessionInSelectedWorkspace(changedSession))
            {
                ApplyMainSessionChanged(changedSession);
            }
            else
            {
                RemoveMainSession(changedSession.SessionId);
            }

            RestoreReconciledSessionSelection(
                selectedSessionId,
                selectedSessionId is not null
                    && FindSessionItem(selectedSessionId.Value) is not null
            );
            RestoreReconciledDisplayedSession(
                displayedSessionId,
                displayedSessionId is not null
                    && FindSessionItem(displayedSessionId.Value) is not null
            );
        }

        var isSelectedSession = SelectedSession?.SessionId == sessionId;
        var isDisplayedSession = DisplayedSession?.SessionId == sessionId;
        if (!isSelectedSession && SelectedSession is not null && IsInSelectedSessionTree(sessionId))
        {
            ReloadPendingPermissionRequests();
        }

        UpdateSessionState(sessionId, markUnread: !isSelectedSession && !isDisplayedSession);
        RefreshVisibleChildSessionLinksIfRelevant(changedSession);
    }

    private void ApplyMainSessionChanged(AgentSessionRecord session)
    {
        var existingIndex = FindSessionIndex(session.SessionId);
        if (existingIndex < 0)
        {
            Sessions.Insert(FindSortedSessionTargetIndex(session), CreateSessionItem(session));
            RefreshSetupState();
            return;
        }

        Sessions[existingIndex].UpdateSession(session);
        Sessions[existingIndex]
            .ApplyCheckpoint(
                _sessionService.GetLatestCheckpoint(session.SessionId),
                markUnread: false
            );

        var targetIndex = FindSortedSessionTargetIndex(session);
        if (targetIndex != existingIndex)
        {
            Sessions.Move(existingIndex, targetIndex);
        }
    }

    private bool IsSessionInSelectedWorkspace(AgentSessionRecord session)
        => SelectedWorkspace is not null
           && string.Equals(session.WorkspaceId, SelectedWorkspace.WorkspaceId, StringComparison.OrdinalIgnoreCase);

    private void RemoveMainSession(Guid sessionId)
    {
        _sessionDrafts.Remove(sessionId);
        var index = FindSessionIndex(sessionId);
        if (index < 0)
        {
            return;
        }

        Sessions.RemoveAt(index);
        if (SelectedSession?.SessionId == sessionId)
        {
            SelectedSession = Sessions.FirstOrDefault();
        }

        if (DisplayedSession?.SessionId == sessionId)
        {
            SetDisplayedSession(SelectedSession);
        }

        RefreshSetupState();
    }

    private int FindSortedSessionTargetIndex(AgentSessionRecord session)
    {
        var targetIndex = 0;
        foreach (var item in Sessions)
        {
            if (item.SessionId == session.SessionId)
            {
                continue;
            }

            if (CompareSessionOrder(session, item.Session) < 0)
            {
                break;
            }

            targetIndex++;
        }

        return targetIndex;
    }

    private static int CompareSessionOrder(AgentSessionRecord left, AgentSessionRecord right)
    {
        var updatedComparison = right.UpdatedAtUtc.CompareTo(left.UpdatedAtUtc);
        return updatedComparison != 0
            ? updatedComparison
            : right.CreatedAtUtc.CompareTo(left.CreatedAtUtc);
    }

    private bool IsInSelectedSessionTree(Guid sessionId)
    {
        if (SelectedSession is null)
        {
            return false;
        }

        var changedSession = _sessionService.GetSession(sessionId);
        var selectedSession = _sessionService.GetSession(SelectedSession.SessionId);
        var selectedRootId = selectedSession?.RootSessionId ?? selectedSession?.SessionId;
        return changedSession is not null
            && selectedRootId is not null
            && (
                changedSession.SessionId == selectedRootId
                || changedSession.ParentSessionId == selectedRootId
                || changedSession.RootSessionId == selectedRootId
            );
    }

    private void RefreshVisibleChildSessionLinksIfRelevant(AgentSessionRecord changedSession)
    {
        if (
            _timeline.Projector.ToolRows.Count == 0
            || changedSession.ParentSessionId is null
            || DisplayedSession is null
        )
        {
            return;
        }

        var displayedRootId = DisplayedSession.Session.RootSessionId ?? DisplayedSession.SessionId;
        var changedRootId = changedSession.RootSessionId ?? changedSession.ParentSessionId;
        if (
            changedSession.ParentSessionId != DisplayedSession.SessionId
            && changedRootId != displayedRootId
        )
        {
            return;
        }

        RefreshVisibleChildSessionLinks();
    }

    private void SyncSelectedSessionState(Guid sessionId)
    {
        if (SelectedSession?.SessionId != sessionId)
        {
            UpdateSessionState(sessionId, markUnread: true);
            return;
        }

        UpdateSessionState(sessionId, markUnread: false);
    }

    private void OnSelectedSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, SelectedSession))
        {
            return;
        }

        if (e.PropertyName == nameof(AgentSessionListItemViewModel.StatusText))
        {
            if (DisplayedSession?.SessionId == SelectedSession?.SessionId)
            {
                StatusText = SelectedSession?.StatusText ?? _globalStatusText;
            }
        }

        if (e.PropertyName == nameof(AgentSessionListItemViewModel.IsRunActive))
        {
            NotifySelectedSessionRunStateChanged();
        }
    }

    private void NotifySelectedSessionRunStateChanged()
    {
        OnPropertyChanged(nameof(IsSelectedSessionRunActive));
        OnPropertyChanged(nameof(IsSelectedSessionRunInactive));
        OnPropertyChanged(nameof(ShowSendAction));
        OnPropertyChanged(nameof(ShowStopAction));
        SendMessageCommand.NotifyCanExecuteChanged();
        UpdateActivityRowForCurrentState();
    }

    private void NotifyDisplayedSessionStateChanged()
    {
        OnPropertyChanged(nameof(IsDisplayedSessionRunActive));
        OnPropertyChanged(nameof(ShowTranscriptSurface));
        OnPropertyChanged(nameof(ShowCollapsedComposer));
        OnPropertyChanged(nameof(ShowExpandedComposer));
        OnPropertyChanged(nameof(ShowSendAction));
        OnPropertyChanged(nameof(ShowStopAction));
        OnPropertyChanged(nameof(CanLoadOlderTranscriptRows));
        OnPropertyChanged(nameof(CanLoadNewerTranscriptRows));
        SendMessageCommand.NotifyCanExecuteChanged();
        UpdateActivityRowForCurrentState();
    }

    private AgentSessionListItemViewModel CreateSessionItem(AgentSessionRecord session)
        => CreateSessionItem(session, _sessionService.GetLatestCheckpoint(session.SessionId));

    private static AgentSessionListItemViewModel CreateSessionItem(
        AgentSessionRecord session,
        AgentRunCheckpointRecord? checkpoint)
    {
        var item = new AgentSessionListItemViewModel(session);
        item.ApplyCheckpoint(checkpoint, markUnread: false);
        return item;
    }

    private void UpdateSessionState(Guid sessionId, bool markUnread)
    {
        var index = FindSessionIndex(sessionId);
        if (index < 0)
        {
            if (
                DisplayedSession?.SessionId == sessionId
                && _sessionService.GetSession(sessionId) is { } displayedSession
            )
            {
                DisplayedSession.UpdateSession(displayedSession);
                var displayedCheckpoint = _sessionService.GetLatestCheckpoint(sessionId);
                DisplayedSession.ApplyCheckpoint(displayedCheckpoint, markUnread: false);
                TrackCheckpointActivity(displayedCheckpoint);
                DisplayedSession.ClearUnreadActivity();
                StatusText = DisplayedSession.StatusText;
                UpdateActivityRowForCurrentState();
                ReloadPendingPermissionRequests();
                NotifyDisplayedSessionStateChanged();
            }

            return;
        }

        var item = Sessions[index];
        var checkpoint = _sessionService.GetLatestCheckpoint(sessionId);
        item.ApplyCheckpoint(checkpoint, markUnread);
        if (DisplayedSession?.SessionId == sessionId && !ReferenceEquals(DisplayedSession, item))
        {
            DisplayedSession.UpdateSession(_sessionService.GetSession(sessionId) ?? item.Session);
            DisplayedSession.ApplyCheckpoint(checkpoint, markUnread: false);
        }

        if (SelectedSession?.SessionId == sessionId)
        {
            ReloadPendingPermissionRequests();
        }

        if (SelectedSession?.SessionId == sessionId && DisplayedSession?.SessionId == sessionId)
        {
            TrackCheckpointActivity(checkpoint);
            item.ClearUnreadActivity();
            StatusText = item.StatusText;
            UpdateActivityRowForCurrentState();
        }

        if (DisplayedSession?.SessionId == sessionId)
        {
            StatusText = DisplayedSession.StatusText;
            NotifyDisplayedSessionStateChanged();
        }
    }

    private void ApplySessionStatus(AgentSessionListItemViewModel session, string statusText)
    {
        session.SetTransientStatus(statusText);
        if (DisplayedSession?.SessionId == session.SessionId)
        {
            StatusText = statusText;
        }
    }
}

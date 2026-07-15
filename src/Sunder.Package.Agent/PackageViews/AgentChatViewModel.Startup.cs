using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _initialization.RunAsync(InitializeCoreAsync, cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            _hasStartupError = false;
            _globalStatusText = string.Empty;
            if (_runtimeAvailability is { IsRuntimeAvailable: false })
            {
                SetGlobalStatus("Agent Runtime is unavailable. Reconnecting...");
            }
            RefreshSetupState();
        }).ConfigureAwait(false);

        var operation = BeginChatSnapshotRequest();
        await LoadAndApplyChatSnapshotAsync(
            new AgentChatSnapshotRequest(InitialTranscriptTurnLimit),
            operation.Generation,
            operation.CancellationToken).ConfigureAwait(false);
    }

    private void ScheduleChatSnapshotRequest(
        string? preferredProfileId,
        string? preferredWorkspaceId,
        Guid? preferredSessionId)
    {
        if (_disposed || !_isInitialized)
        {
            return;
        }

        var operation = BeginChatSnapshotRequest();
        var request = new AgentChatSnapshotRequest(
            InitialTranscriptTurnLimit,
            preferredProfileId,
            preferredWorkspaceId,
            preferredSessionId);
        _backgroundTasks.Run(async _ =>
        {
            try
            {
                await LoadAndApplyChatSnapshotAsync(
                    request,
                    operation.Generation,
                    operation.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
            {
            }
        });
    }

    private (int Generation, CancellationToken CancellationToken) BeginChatSnapshotRequest()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        int generation;
        lock (_chatSnapshotRequestLock)
        {
            previous = _chatSnapshotRequestCancellation;
            current = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _chatSnapshotRequestCancellation = current;
            generation = ++_chatSnapshotRequestGeneration;
        }
        previous?.Cancel();
        previous?.Dispose();
        return (generation, current.Token);
    }

    private bool IsCurrentChatSnapshotRequest(int generation)
    {
        lock (_chatSnapshotRequestLock)
        {
            return !_disposed && generation == _chatSnapshotRequestGeneration;
        }
    }

    private async Task LoadAndApplyChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        int generation,
        CancellationToken cancellationToken)
    {
        var snapshot = _chatSnapshotGateway is not null
            ? await _chatSnapshotGateway.LoadChatSnapshotAsync(request, cancellationToken).ConfigureAwait(false)
            : await LoadInProcessChatSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
        var applied = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeOnUiThreadAsync(() =>
            {
                if (!IsCurrentChatSnapshotRequest(generation))
                {
                    return;
                }

                ApplyChatSnapshot(snapshot, forceTranscriptReplacement: false);
                applied = true;
            }).ConfigureAwait(false);
            if (applied)
            {
                await PersistAppliedSelectionAsync(snapshot, generation).ConfigureAwait(false);
            }
        }
        finally
        {
            _chatSnapshotGateway?.CompleteChatSnapshot(snapshot, applied);
        }
    }

    private async Task PersistAppliedSelectionAsync(
        AgentChatSnapshotProjection snapshot,
        int generation)
    {
        if (_selectionState is null)
        {
            return;
        }

        await _selectionPersistenceGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            if (!IsCurrentChatSnapshotRequest(generation))
            {
                return;
            }

            await _selectionState.SaveSelectedProfileIdAsync(
                snapshot.SelectedProfile?.ProfileId,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            await _selectionState.SaveSelectedWorkspaceIdAsync(
                snapshot.SelectedWorkspace?.WorkspaceId,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            await _selectionState.SaveSelectedSessionIdAsync(
                snapshot.SelectedWorkspace?.WorkspaceId,
                snapshot.SelectedSession?.Session.SessionId,
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _selectionPersistenceGate.Release();
        }
    }

    private async Task<AgentChatSnapshotProjection> LoadInProcessChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        await _workspaceService.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var storedProfileIdTask = _selectionState is null
            ? Task.FromResult<string?>(null)
            : _selectionState.GetSelectedProfileIdAsync(cancellationToken);
        var storedWorkspaceIdTask = _selectionState is null
            ? Task.FromResult<string?>(null)
            : _selectionState.GetSelectedWorkspaceIdAsync(cancellationToken);
        await Task.WhenAll(storedProfileIdTask, storedWorkspaceIdTask).ConfigureAwait(false);
        var selectedProfileId = NormalizeSelection(request.PreferredProfileId)
                                ?? await storedProfileIdTask.ConfigureAwait(false);
        var selectedWorkspaceId = NormalizeSelection(request.PreferredWorkspaceId)
                                  ?? await storedWorkspaceIdTask.ConfigureAwait(false);
        var profiles = _profileService.ListProfiles();
        var workspaces = _workspaceService.ListWorkspaces();
        var selectedProfile = profiles.FirstOrDefault(profile => string.Equals(
                                  profile.ProfileId,
                                  selectedProfileId,
                                  StringComparison.OrdinalIgnoreCase))
                              ?? profiles.FirstOrDefault();
        var selectedWorkspace = workspaces.FirstOrDefault(workspace => string.Equals(
                                    workspace.WorkspaceId,
                                    selectedWorkspaceId,
                                    StringComparison.OrdinalIgnoreCase))
                                ?? workspaces.FirstOrDefault();
        var storedSessionId = selectedWorkspace is null || _selectionState is null
            ? null
            : await _selectionState.GetSelectedSessionIdAsync(
                selectedWorkspace.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
        var selectedSessionId = request.PreferredSessionId ?? storedSessionId;
        IReadOnlyList<AgentSessionRecord> sessionRecords = selectedWorkspace is null
            ? []
            : _sessionService.ListSessionsForWorkspace(selectedWorkspace.WorkspaceId);
        var rootSessions = sessionRecords.Where(static session => session.ParentSessionId is null).ToArray();
        var selectedSessionRecord = ResolveChatRootSession(sessionRecords, rootSessions, selectedSessionId);
        var sessionSnapshots = sessionRecords
            .Select(session => new AgentSessionSnapshot(
                session,
                _sessionService.GetLatestCheckpoint(session.SessionId)))
            .ToArray();
        var selectedSession = selectedSessionRecord is null
            ? null
            : sessionSnapshots.First(item => item.Session.SessionId == selectedSessionRecord.SessionId);
        var limit = Math.Clamp(request.InitialTranscriptLimit, 1, 500);
        IReadOnlyList<AgentTurnRecord> turns = selectedSession is null
            ? []
            : _sessionService.ListRecentTurns(selectedSession.Session.SessionId, limit + 1);
        var hasMoreTurns = turns.Count > limit;
        if (hasMoreTurns)
        {
            turns = turns.Skip(turns.Count - limit).ToArray();
        }

        var snapshot = new AgentChatSnapshotProjection(
            0,
            profiles,
            workspaces,
            workspaces.SelectMany(workspace => _workspaceService.ListBindings(workspace.WorkspaceId)).ToArray(),
            selectedProfile,
            selectedWorkspace,
            selectedSession,
            sessionSnapshots,
            new AgentTranscriptPage(0, turns, hasMoreTurns),
            new AgentChatPermissionProjection(
                selectedSession is null
                    ? null
                    : _permissionService.GetSessionState(selectedSession.Session.SessionId),
                selectedSession is null
                    ? []
                    : _permissionService.ListPendingRequestsForSessionTree(selectedSession.Session.SessionId)));
        return snapshot;
    }

    private void ApplyChatSnapshot(
        AgentChatSnapshotProjection snapshot,
        bool forceTranscriptReplacement)
    {
        if (_disposed)
        {
            return;
        }

        var wasInitialized = _isInitialized;
        var previousWorkspaceId = SelectedWorkspace?.WorkspaceId;
        var previousSessionId = SelectedSession?.SessionId;
        if (previousSessionId is { } capturedSessionId)
        {
            _sessionDrafts[capturedSessionId] = _composer.Text;
        }
        InstallSnapshotSessionCache(snapshot.WorkspaceSessions);
        var nextWorkspaceId = snapshot.SelectedWorkspace?.WorkspaceId;
        var nextSessionId = snapshot.SelectedSession?.Session.SessionId;
        var isSessionReplacement = previousSessionId != nextSessionId
                                   || !string.Equals(
                                       previousWorkspaceId,
                                       nextWorkspaceId,
                                       StringComparison.OrdinalIgnoreCase);
        _isApplyingChatSnapshot = true;
        _suppressWorkspaceSelection = true;
        try
        {
            if (_observedSelectedSession is not null)
            {
                _observedSelectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
                _observedSelectedSession = null;
            }

            ReconcileProfiles(snapshot.Profiles);
            ReconcileWorkspaces(snapshot.Workspaces);
            SelectedProfile = snapshot.SelectedProfile is null
                ? null
                : Profiles.FirstOrDefault(profile => string.Equals(
                    profile.ProfileId,
                    snapshot.SelectedProfile.ProfileId,
                    StringComparison.OrdinalIgnoreCase));
            SelectedWorkspace = snapshot.SelectedWorkspace is null
                ? null
                : Workspaces.FirstOrDefault(workspace => string.Equals(
                    workspace.WorkspaceId,
                    snapshot.SelectedWorkspace.WorkspaceId,
                    StringComparison.OrdinalIgnoreCase));

            ReconcileSessionSnapshots(snapshot.WorkspaceSessions);
            SelectedSession = snapshot.SelectedSession is null
                ? null
                : Sessions.FirstOrDefault(session =>
                    session.SessionId == snapshot.SelectedSession.Session.SessionId);
            DisplayedSession = SelectedSession;
            if (isSessionReplacement)
            {
                ClearRollbackStateOnly();
                ClearPendingAttachments();
                IsComposerDropTargetActive = false;
                CloseAttachmentPreview();
            }
            if (SelectedSession is not null)
            {
                SelectedSession.IsSelected = true;
                SelectedSession.ClearUnreadActivity();
                _observedSelectedSession = SelectedSession;
                _observedSelectedSession.PropertyChanged += OnSelectedSessionPropertyChanged;
                if (_sessionDrafts.TryGetValue(SelectedSession.SessionId, out var draft))
                {
                    SelectedSession.DraftMessage = draft;
                }
                _composer.Text = SelectedSession.DraftMessage;
            }
            else
            {
                _composer.Text = string.Empty;
            }

            _permissionPanel.ApplySnapshot(
                SelectedSession?.SessionId,
                snapshot.Permissions.SessionState,
                snapshot.Permissions.PendingRequests);
            ApplySnapshotTranscript(snapshot, forceTranscriptReplacement || !wasInitialized);
        }
        finally
        {
            _suppressWorkspaceSelection = false;
            _isApplyingChatSnapshot = false;
        }

        _isInitialized = true;
        OnPropertyChanged(nameof(DraftMessage));
        NotifyProfileStateChanged();
        NotifyWorkspaceStateChanged();
        RefreshWorkspacePathChips();
        NotifyPermissionPanelChanged();
        NotifySelectedSessionRunStateChanged();
        NotifyDisplayedSessionStateChanged();
        CreateSessionCommand.NotifyCanExecuteChanged();
        RefreshSetupState();
        if (DisplayedSession is not null)
        {
            StatusText = DisplayedSession.StatusText;
        }
        if (!string.Equals(
                previousWorkspaceId,
                SelectedWorkspace?.WorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            ScheduleSelectedWorkspaceWarmup();
        }
    }

    private void InstallSnapshotSessionCache(IReadOnlyList<AgentSessionSnapshot> sessions)
    {
        _snapshotSessions.Clear();
        foreach (var session in sessions)
        {
            _snapshotSessions[session.Session.SessionId] = session;
        }
    }

    private void ApplySnapshotTranscript(
        AgentChatSnapshotProjection snapshot,
        bool forceReplacement)
    {
        if (DisplayedSession is null)
        {
            if (_timeline.SessionId is not null)
            {
                _timeline.ClearSession();
            }
            return;
        }

        var replacedTranscript = forceReplacement || _timeline.SessionId != DisplayedSession.SessionId;
        if (replacedTranscript)
        {
            var ticket = _timeline.BeginInitialLoad(DisplayedSession.SessionId);
            _timeline.TryCompleteInitialLoad(
                ticket,
                snapshot.InitialTranscript.Turns,
                snapshot.InitialTranscript.HasMore);
        }
        else
        {
            var currentTurns = _timeline.Projector.TurnWindow.OrderedTurns()
                .ToDictionary(turn => turn.TurnId);
            foreach (var turn in snapshot.InitialTranscript.Turns)
            {
                if (!currentTurns.TryGetValue(turn.TurnId, out var current)
                    || current.UpdatedAtUtc != turn.UpdatedAtUtc)
                {
                    _timeline.ApplyLiveTurn(turn);
                }
            }
        }

        if (replacedTranscript)
        {
            TrackCheckpointActivity(snapshot.SelectedSession?.Checkpoint);
            ApplyRunActivityState();
        }
        StatusText = DisplayedSession.StatusText;
    }

    private void ReconcileProfiles(IReadOnlyList<AgentProfileRecord> profiles)
    {
        var desiredIds = profiles.Select(profile => profile.ProfileId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Profiles.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(Profiles[index].ProfileId))
            {
                Profiles.RemoveAt(index);
            }
        }
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var existingIndex = -1;
            for (var candidate = 0; candidate < Profiles.Count; candidate++)
            {
                if (string.Equals(
                        Profiles[candidate].ProfileId,
                        profile.ProfileId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = candidate;
                    break;
                }
            }
            if (existingIndex < 0)
            {
                Profiles.Insert(index, profile);
            }
            else
            {
                if (!Equals(Profiles[existingIndex], profile))
                {
                    Profiles[existingIndex] = profile;
                }
                if (existingIndex != index)
                {
                    Profiles.Move(existingIndex, index);
                }
            }
        }
    }

    private void OnChatSnapshotReloaded(AgentChatSnapshotProjection snapshot)
    {
        if (_disposed)
        {
            return;
        }

        var operation = BeginChatSnapshotRequest();
        _backgroundTasks.Run(async _ =>
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (IsCurrentChatSnapshotRequest(operation.Generation))
                {
                    ApplyChatSnapshot(snapshot, forceTranscriptReplacement: true);
                }
            }).ConfigureAwait(false);
        });
    }

    private static AgentSessionRecord? ResolveChatRootSession(
        IReadOnlyList<AgentSessionRecord> workspaceSessions,
        IReadOnlyList<AgentSessionRecord> rootSessions,
        Guid? selectedSessionId)
    {
        var selected = selectedSessionId is null
            ? null
            : workspaceSessions.FirstOrDefault(session => session.SessionId == selectedSessionId.Value);
        if (selected?.ParentSessionId is not null)
        {
            var rootSessionId = selected.RootSessionId ?? selected.ParentSessionId.Value;
            selected = rootSessions.FirstOrDefault(session => session.SessionId == rootSessionId);
        }

        return selected is { ParentSessionId: null }
            ? selected
            : rootSessions.FirstOrDefault();
    }

    private static string? NormalizeSelection(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

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

    internal void ReportStartupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (_runtimeFailureClassifier?.IsRetryableRuntimeFailure(exception) == true)
        {
            Interlocked.Exchange(ref _startupRecoveryPending, 1);
            RunOnUiThread(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _hasStartupError = false;
                SetGlobalStatus("Agent Runtime is unavailable. Reconnecting...");
                RefreshSetupState();
            });
            RequestStartupRecovery();
            return;
        }

        Interlocked.Exchange(ref _startupRecoveryPending, 0);
        RunOnUiThread(ApplyPermanentStartupFailure);
    }

    private void TryScheduleStartupRecovery()
    {
        var connectionGeneration = Volatile.Read(ref _startupRecoveryConnectionGeneration);
        if (_disposed
            || Volatile.Read(ref _startupRecoveryPending) == 0
            || _runtimeAvailability?.ConnectionState != AgentRuntimeConnectionState.Connected
            || connectionGeneration == Volatile.Read(ref _startupRecoveryAttemptedConnectionGeneration)
            || Interlocked.CompareExchange(ref _startupRecoveryScheduled, 1, 0) != 0)
        {
            return;
        }

        connectionGeneration = Volatile.Read(ref _startupRecoveryConnectionGeneration);
        Volatile.Write(ref _startupRecoveryAttemptedConnectionGeneration, connectionGeneration);
        _backgroundTasks.Run(RecoverStartupAsync);
    }

    private void RequestStartupRecovery()
    {
        if (_disposed
            || _runtimeAvailability?.ConnectionState != AgentRuntimeConnectionState.Connected)
        {
            return;
        }

        Interlocked.Increment(ref _startupRecoveryConnectionGeneration);
        TryScheduleStartupRecovery();
    }

    private async Task RecoverStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _startupRecoveryPending, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_runtimeFailureClassifier?.IsRetryableRuntimeFailure(
                    exception,
                    cancellationToken) == true)
            {
                Interlocked.Exchange(ref _startupRecoveryPending, 1);
                await InvokeOnUiThreadAsync(() =>
                {
                    if (!_disposed)
                    {
                        _hasStartupError = false;
                        SetGlobalStatus("Agent Runtime is unavailable. Reconnecting...");
                        RefreshSetupState();
                    }
                }).ConfigureAwait(false);
            }
            else
            {
                Interlocked.Exchange(ref _startupRecoveryPending, 0);
                await InvokeOnUiThreadAsync(ApplyPermanentStartupFailure).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _startupRecoveryScheduled, 0);
            TryScheduleStartupRecovery();
        }
    }

    private void ApplyPermanentStartupFailure()
    {
        if (_disposed)
        {
            return;
        }

        _hasStartupError = true;
        SetGlobalStatus("Unable to load Agent Chat. Navigate away and return to retry.");
        RefreshSetupState();
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
                await PersistAppliedSelectionAsync(snapshot, generation, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _chatSnapshotGateway?.CompleteChatSnapshot(snapshot, applied);
        }
    }

    private async Task PersistAppliedSelectionAsync(
        AgentChatSnapshotProjection snapshot,
        int generation,
        CancellationToken cancellationToken)
        => await PersistAppliedSelectionAsync(
            snapshot.SelectedProfile?.ProfileId,
            snapshot.SelectedWorkspace?.WorkspaceId,
            snapshot.SelectedSession?.Session.SessionId,
            generation,
            cancellationToken).ConfigureAwait(false);

    private async Task PersistAppliedSelectionAsync(
        string? profileId,
        string? workspaceId,
        Guid? sessionId,
        int generation,
        CancellationToken cancellationToken)
    {
        if (_selectionState is null)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var operationCancellation = linkedCancellation.Token;
        await _selectionPersistenceGate.WaitAsync(operationCancellation).ConfigureAwait(false);
        try
        {
            operationCancellation.ThrowIfCancellationRequested();
            if (!IsCurrentChatSnapshotRequest(generation))
            {
                return;
            }

            await _selectionState.SaveSelectedProfileIdAsync(
                profileId,
                operationCancellation).ConfigureAwait(false);
            operationCancellation.ThrowIfCancellationRequested();
            await _selectionState.SaveSelectedWorkspaceIdAsync(
                workspaceId,
                operationCancellation).ConfigureAwait(false);
            operationCancellation.ThrowIfCancellationRequested();
            await _selectionState.SaveSelectedSessionIdAsync(
                workspaceId,
                sessionId,
                operationCancellation).ConfigureAwait(false);
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
            || !request.IncludeInitialTranscript
            ? []
            : _sessionService is IAgentTranscriptHeaderGateway transcriptHeaders
                ? transcriptHeaders.ListRecentTranscriptHeaders(
                    selectedSession.Session.SessionId,
                    limit + 1)
                : _sessionService.ListRecentTurns(selectedSession.Session.SessionId, limit + 1)
                    .Select(TranscriptTurnTransportProjection.ProjectToolHeaders)
                    .ToArray();
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
            new AgentTranscriptPage(
                0,
                turns,
                hasMoreTurns,
                turns.Count == 0 ? null : TranscriptPageCursor.FromTurn(turns[0])),
            new AgentChatPermissionProjection(
                0,
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
        => ApplyChatSnapshotCore(snapshot, forceTranscriptReplacement, applyTranscript: true);

    private void ApplyChatSnapshotCore(
        AgentChatSnapshotProjection snapshot,
        bool forceTranscriptReplacement,
        bool applyTranscript)
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

            var runtimeInstanceChanged = !string.Equals(
                snapshot.RuntimeInstanceId,
                _appliedRuntimeInstanceId,
                StringComparison.Ordinal);
            if (runtimeInstanceChanged)
            {
                _appliedRuntimeInstanceId = snapshot.RuntimeInstanceId;
                _appliedPermissionRevision = 0;
                Interlocked.Increment(ref _permissionRequestGeneration);
            }
            if (runtimeInstanceChanged
                || isSessionReplacement
                || snapshot.Permissions.Revision >= _appliedPermissionRevision)
            {
                _appliedPermissionRevision = snapshot.Permissions.Revision;
                _permissionPanel.ApplySnapshot(
                    SelectedSession?.SessionId,
                    snapshot.Permissions.SessionState,
                    snapshot.Permissions.PendingRequests);
            }
            if (applyTranscript)
            {
                ApplySnapshotTranscript(snapshot, forceTranscriptReplacement || !wasInitialized);
            }
        }
        finally
        {
            _suppressWorkspaceSelection = false;
            _isApplyingChatSnapshot = false;
        }

        _isInitialized = true;
        ScheduleCompletedSubmissionReconciliation();
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

        var isSessionReplacement = _timeline.SessionId != DisplayedSession.SessionId;
        var replacedTranscript = forceReplacement || isSessionReplacement;
        if (replacedTranscript)
        {
            var previousRunActivityTurns = isSessionReplacement
                ? null
                : SelectRunActivityTurns(_timeline.Projector.TurnWindow.OrderedTurns());
            if (isSessionReplacement)
            {
                _runActivity.Reset();
            }
            var ticket = _timeline.BeginInitialLoad(
                DisplayedSession.SessionId,
                forceReplacement);
            var applied = _timeline.TryCompleteInitialLoad(
                ticket,
                snapshot.InitialTranscript.Turns,
                snapshot.InitialTranscript.HasMore,
                snapshot.InitialTranscript.Continuation);
            if (applied && previousRunActivityTurns is not null)
            {
                ReconcileRunActivityAfterAuthoritativeReplacement(previousRunActivityTurns);
            }
        }
        else
        {
            var currentTurns = _timeline.Projector.TurnWindow.OrderedTurns()
                .ToDictionary(turn => turn.TurnId);
            foreach (var turn in snapshot.InitialTranscript.Turns)
            {
                if (!currentTurns.TryGetValue(turn.TurnId, out var current)
                    || current.ContentRevision != turn.ContentRevision
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

    private void ReconcileRunActivityAfterAuthoritativeReplacement(
        IReadOnlyList<AgentTurnRecord> previousTurns)
    {
        var currentTurns = SelectRunActivityTurns(_timeline.Projector.TurnWindow.OrderedTurns());
        if (HaveSameRunActivityTurns(previousTurns, currentTurns))
        {
            return;
        }

        _runActivity.Reset();
        foreach (var turn in currentTurns)
        {
            _runActivity.TrackTurn(turn, scheduleQuietTimer: true);
        }
    }

    private static AgentTurnRecord[] SelectRunActivityTurns(IEnumerable<AgentTurnRecord> turns)
    {
        var orderedTurns = turns
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var latestUserIndex = Array.FindLastIndex(
            orderedTurns,
            turn => turn.Role == AgentMessageRole.User);
        return latestUserIndex < 0 ? orderedTurns : orderedTurns[latestUserIndex..];
    }

    private static bool HaveSameRunActivityTurns(
        IReadOnlyList<AgentTurnRecord> first,
        IReadOnlyList<AgentTurnRecord> second)
        => first.Count == second.Count
           && first.Zip(second).All(pair =>
               pair.First.TurnId == pair.Second.TurnId
               && pair.First.Role == pair.Second.Role
               && pair.First.Kind == pair.Second.Kind
               && pair.First.Items.SequenceEqual(pair.Second.Items));

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
        EnqueueTranscriptBoundary(() =>
        {
            if (IsCurrentChatSnapshotRequest(operation.Generation))
            {
                ApplyChatSnapshot(snapshot, forceTranscriptReplacement: true);
            }
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

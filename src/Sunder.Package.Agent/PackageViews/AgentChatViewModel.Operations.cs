using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private static readonly PresentationOperation CreateSessionOperation = new(
        "agent-chat.create-session",
        "The session could not be created. Agent Chat refreshed its current state.");
    private static readonly PresentationOperation RenameSessionOperation = new(
        "agent-chat.rename-session",
        "The session could not be renamed. Agent Chat refreshed its current state.");
    private static readonly PresentationOperation DeleteSessionOperation = new(
        "agent-chat.delete-session",
        "The session deletion could not be confirmed. Agent Chat refreshed its current state.");
    private static readonly PresentationOperation SendMessageOperation = new(
        "agent-chat.send-message",
        "The message could not be sent. Agent Chat reconciled the composer and session state.");
    private static readonly PresentationOperation StopRunOperation = new(
        "agent-chat.stop-run",
        "The run could not be stopped. Agent Chat refreshed the session state.");
    private static readonly PresentationOperation ChangeSessionPermissionOperation = new(
        "agent-chat.change-session-permission",
        "The session permission could not be changed. Agent Chat refreshed permission state.");
    private static readonly PresentationOperation ResolvePermissionOperation = new(
        "agent-chat.resolve-permission",
        "The permission request could not be resolved. Agent Chat refreshed permission state.");
    private static readonly PresentationOperation RollbackOperation = new(
        "agent-chat.rollback",
        "The earlier message could not be prepared for editing. Agent Chat refreshed the session.");
    private static readonly PresentationOperation OpenChildSessionOperation = new(
        "agent-chat.open-child-session",
        "The subsession could not be opened. Agent Chat refreshed its current state.");
    private static readonly PresentationOperation AttachmentPreviewOperation = new(
        "agent-chat.attachment-preview",
        "The attachment preview could not be opened.");

    private readonly PresentationOperationObserver _operations;
    private readonly SemaphoreSlim _unrestrictedModeMutationGate = new(1, 1);
    private long _unrestrictedModeIntentRevision;

    private Task RunOperationAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile = null,
        CancellationToken cancellationToken = default)
        => _operations.RunAsync(operation, action, reconcile, cancellationToken);

    private async Task ReconcileChatSnapshotAsync(
        CancellationToken cancellationToken,
        Guid? preferredSessionId = null)
    {
        AgentChatSnapshotRequest? request = null;
        await InvokeOnUiThreadAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            request = new AgentChatSnapshotRequest(
                InitialTranscriptTurnLimit,
                SelectedProfile?.ProfileId,
                SelectedWorkspace?.WorkspaceId,
                preferredSessionId ?? SelectedSession?.SessionId);
        }).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        var snapshotOperation = BeginChatSnapshotRequest();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            snapshotOperation.CancellationToken);
        await LoadAndApplyChatSnapshotAsync(
            request,
            snapshotOperation.Generation,
            linkedCancellation.Token).ConfigureAwait(false);
    }

    private async Task ReconcileSessionPermissionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken,
        long? requiredIntentRevision = null)
    {
        if (_chatPermissionCommandGateway is null)
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (!_disposed
                    && SelectedSession?.SessionId == sessionId
                    && (requiredIntentRevision is null
                        || requiredIntentRevision == Volatile.Read(ref _unrestrictedModeIntentRevision)))
                {
                    _permissionPanel.Reload();
                    OnPropertyChanged(nameof(IsUnrestrictedModeEnabled));
                    NotifyPermissionPanelChanged();
                }
            }).ConfigureAwait(false);
            return;
        }

        var projection = await _chatPermissionCommandGateway
            .LoadSessionPermissionsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            if (_disposed
                || SelectedSession?.SessionId != sessionId
                || requiredIntentRevision is not null
                && requiredIntentRevision != Volatile.Read(ref _unrestrictedModeIntentRevision)
                || projection.Revision < _appliedPermissionRevision)
            {
                return;
            }

            _appliedPermissionRevision = projection.Revision;
            _permissionPanel.ApplySnapshot(
                sessionId,
                projection.SessionState,
                projection.PendingRequests);
            OnPropertyChanged(nameof(IsUnrestrictedModeEnabled));
            NotifyPermissionPanelChanged();
        }).ConfigureAwait(false);
    }

    private FinalSessionDeletionAuthority? CaptureFinalSessionDeletionAuthority(Guid sessionId)
    {
        if (Sessions.Count != 1 || Sessions[0].SessionId != sessionId)
        {
            return null;
        }

        lock (_chatSnapshotRequestLock)
        {
            return new FinalSessionDeletionAuthority(
                sessionId,
                SelectedWorkspace?.WorkspaceId,
                _appliedRuntimeInstanceId,
                _chatSnapshotRequestGeneration);
        }
    }

    private async Task ApplySuccessfulFinalSessionDeletionAsync(
        FinalSessionDeletionAuthority authority,
        CancellationToken cancellationToken)
    {
        var applied = false;
        await InvokeOnUiThreadAsync(() =>
        {
            if (IsFinalSessionDeletionAuthorityCurrent(authority, requireDeletedEmptyState: false))
            {
                ApplySuccessfulFinalSessionDeletion(authority.SessionId);
                applied = true;
            }
        }).ConfigureAwait(false);
        if (!applied || _selectionState is null)
        {
            return;
        }

        await _selectionPersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await IsFinalSessionDeletionAuthorityCurrentAsync(authority).ConfigureAwait(false))
            {
                return;
            }
            await _selectionState.SaveSelectedSessionIdAsync(
                authority.WorkspaceId,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _selectionPersistenceGate.Release();
        }
    }

    private async Task<bool> IsFinalSessionDeletionAuthorityCurrentAsync(
        FinalSessionDeletionAuthority authority)
    {
        var isCurrent = false;
        await InvokeOnUiThreadAsync(() =>
        {
            isCurrent = IsFinalSessionDeletionAuthorityCurrent(
                authority,
                requireDeletedEmptyState: true);
        }).ConfigureAwait(false);
        return isCurrent;
    }

    private bool IsFinalSessionDeletionAuthorityCurrent(
        FinalSessionDeletionAuthority authority,
        bool requireDeletedEmptyState)
    {
        int generation;
        lock (_chatSnapshotRequestLock)
        {
            generation = _chatSnapshotRequestGeneration;
        }

        if (_disposed
            || generation != authority.SnapshotGeneration
            || !string.Equals(
                _appliedRuntimeInstanceId,
                authority.RuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                SelectedWorkspace?.WorkspaceId,
                authority.WorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return requireDeletedEmptyState
            ? SelectedSession is null && Sessions.Count == 0
            : SelectedSession?.SessionId == authority.SessionId
              && Sessions.Count == 1
              && Sessions[0].SessionId == authority.SessionId;
    }

    private void ApplySuccessfulFinalSessionDeletion(Guid sessionId)
    {
        if (_disposed)
        {
            return;
        }

        if (_observedSelectedSession is not null)
        {
            _observedSelectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;
            _observedSelectedSession = null;
        }

        _isApplyingChatSnapshot = true;
        try
        {
            foreach (var session in Sessions)
            {
                session.IsSelected = false;
            }
            Sessions.Clear();
            var deletedSessionIds = _snapshotSessions.Values
                .Where(snapshot => snapshot.Session.SessionId == sessionId
                                   || snapshot.Session.RootSessionId == sessionId)
                .Select(snapshot => snapshot.Session.SessionId)
                .Append(sessionId)
                .Distinct()
                .ToArray();
            foreach (var deletedSessionId in deletedSessionIds)
            {
                _snapshotSessions.Remove(deletedSessionId);
                _sessionDrafts.Remove(deletedSessionId);
            }
            SelectedSession = null;
            DisplayedSession = null;
            _composer.Text = string.Empty;
            ClearRollbackStateOnly();
            ClearPendingAttachments();
            IsComposerDropTargetActive = false;
            CloseAttachmentPreview();
            _permissionPanel.ApplySnapshot(null, null, []);
            _appliedPermissionRevision = 0;
            Interlocked.Increment(ref _permissionRequestGeneration);
            if (_timeline.SessionId is not null)
            {
                _timeline.ClearSession();
            }
            _runActivity.Reset();
            ApplyRunActivityState();
            _globalStatusText = string.Empty;
        }
        finally
        {
            _isApplyingChatSnapshot = false;
        }

        OnPropertyChanged(nameof(DraftMessage));
        NotifyPermissionPanelChanged();
        NotifySelectedSessionRunStateChanged();
        NotifyDisplayedSessionStateChanged();
        RefreshSetupState();
    }

    private readonly record struct FinalSessionDeletionAuthority(
        Guid SessionId,
        string? WorkspaceId,
        string? RuntimeInstanceId,
        int SnapshotGeneration);
}

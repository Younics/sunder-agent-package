using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    public bool IsUnrestrictedModeEnabled
    {
        get => _permissionPanel.IsUnrestrictedModeEnabled;
        set
        {
            if (_permissionPanel.IsUnrestrictedModeEnabled == value)
            {
                return;
            }

            _permissionPanel.SetUnrestrictedModeValue(value);
            OnPropertyChanged();
            if (SelectedSession is not { } selectedSession)
            {
                return;
            }
            if (_chatPermissionCommandGateway is null)
            {
                _permissionService.SetSessionUnrestrictedMode(selectedSession.SessionId, value);
                OnIsUnrestrictedModeEnabledChanged(
                    value,
                    AgentPermissionPanelState.DescribeUnrestrictedMode(value));
                return;
            }

            var sessionId = selectedSession.SessionId;
            var runtimeInstanceId = _appliedRuntimeInstanceId;
            var intentRevision = Interlocked.Increment(ref _unrestrictedModeIntentRevision);
            _operations.Submit(
                ChangeSessionPermissionOperation,
                async cancellationToken =>
                {
                    await _unrestrictedModeMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (intentRevision != Volatile.Read(ref _unrestrictedModeIntentRevision))
                        {
                            return;
                        }

                        var permissions = await _chatPermissionCommandGateway.SetSessionUnrestrictedModeAsync(
                            sessionId,
                            value,
                            cancellationToken).ConfigureAwait(false);
                        await InvokeOnUiThreadAsync(() =>
                        {
                            if (SelectedSession?.SessionId != sessionId
                                || intentRevision != Volatile.Read(ref _unrestrictedModeIntentRevision)
                                || !string.Equals(
                                    runtimeInstanceId,
                                    _appliedRuntimeInstanceId,
                                    StringComparison.Ordinal)
                                || permissions.Revision < _appliedPermissionRevision)
                            {
                                return;
                            }
                            _appliedPermissionRevision = permissions.Revision;
                            _permissionPanel.ApplySnapshot(
                                sessionId,
                                permissions.SessionState,
                                permissions.PendingRequests);
                            OnPropertyChanged(nameof(IsUnrestrictedModeEnabled));
                            NotifyPermissionPanelChanged();
                            OnIsUnrestrictedModeEnabledChanged(
                                value,
                                AgentPermissionPanelState.DescribeUnrestrictedMode(value));
                        }).ConfigureAwait(false);
                    }
                    finally
                    {
                        _unrestrictedModeMutationGate.Release();
                    }
                },
                cancellationToken => ReconcileSessionPermissionsAsync(
                    sessionId,
                    cancellationToken,
                    intentRevision));
        }
    }

    public bool HasPendingPermissionRequests => _permissionPanel.HasRequests;

    private void OnIsUnrestrictedModeEnabledChanged(bool value, string status)
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        ApplySessionStatus(SelectedSession, status);
    }

    [RelayCommand]
    private Task ApprovePermissionAsync(AgentPendingPermissionRequestRecord? request)
        => RunOperationAsync(
            ResolvePermissionOperation,
            cancellationToken => ApprovePermissionCoreAsync(request, approveForSession: false, cancellationToken),
            cancellationToken => ReconcileSelectedSessionPermissionsAsync(request, cancellationToken));

    private async Task ApprovePermissionCoreAsync(
        AgentPendingPermissionRequestRecord? request,
        bool approveForSession,
        CancellationToken cancellationToken)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var summary = await _permissionPanel.ApproveAsync(request, approveForSession)
            .ConfigureAwait(false);
        await ReconcileSessionPermissionsAsync(selectedSession.SessionId, cancellationToken)
            .ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            NotifyPermissionPanelChanged();
            ApplySessionStatus(selectedSession, summary);
            SyncSelectedSessionState(selectedSession.SessionId);
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task ApprovePermissionForSessionAsync(AgentPendingPermissionRequestRecord? request)
        => RunOperationAsync(
            ResolvePermissionOperation,
            cancellationToken => ApprovePermissionCoreAsync(request, approveForSession: true, cancellationToken),
            cancellationToken => ReconcileSelectedSessionPermissionsAsync(request, cancellationToken));

    [RelayCommand]
    private Task DenyPermissionAsync(AgentPendingPermissionRequestRecord? request)
        => RunOperationAsync(
            ResolvePermissionOperation,
            cancellationToken => DenyPermissionCoreAsync(request, cancellationToken),
            cancellationToken => ReconcileSelectedSessionPermissionsAsync(request, cancellationToken));

    private async Task DenyPermissionCoreAsync(
        AgentPendingPermissionRequestRecord? request,
        CancellationToken cancellationToken)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var summary = await _permissionPanel.DenyAsync(request).ConfigureAwait(false);
        await ReconcileSessionPermissionsAsync(selectedSession.SessionId, cancellationToken)
            .ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            ApplySessionStatus(selectedSession, summary);
            NotifyPermissionPanelChanged();
            SyncSelectedSessionState(selectedSession.SessionId);
        }).ConfigureAwait(false);
    }

    private Task ReconcileSelectedSessionPermissionsAsync(
        AgentPendingPermissionRequestRecord? request,
        CancellationToken cancellationToken)
        => request is null
            ? Task.CompletedTask
            : ReconcileSessionPermissionsAsync(request.SessionId, cancellationToken);
}

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
            _backgroundTasks.Run(async cancellationToken =>
            {
                var permissions = await _chatPermissionCommandGateway.SetSessionUnrestrictedModeAsync(
                    sessionId,
                    value,
                    cancellationToken).ConfigureAwait(false);
                await InvokeOnUiThreadAsync(() =>
                {
                    if (SelectedSession?.SessionId != sessionId
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
            });
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
    private async Task ApprovePermissionAsync(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        var summary = await _permissionPanel.ApproveAsync(request, approveForSession: false)
            .ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            NotifyPermissionPanelChanged();
            ApplySessionStatus(selectedSession, summary);
            SyncSelectedSessionState(selectedSession.SessionId);
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ApprovePermissionForSessionAsync(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        var summary = await _permissionPanel.ApproveAsync(request, approveForSession: true)
            .ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            NotifyPermissionPanelChanged();
            ApplySessionStatus(selectedSession, summary);
            SyncSelectedSessionState(selectedSession.SessionId);
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DenyPermissionAsync(AgentPendingPermissionRequestRecord? request)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null
            || request is null
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        var summary = await _permissionPanel.DenyAsync(request).ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            ApplySessionStatus(selectedSession, summary);
            NotifyPermissionPanelChanged();
            SyncSelectedSessionState(selectedSession.SessionId);
        }).ConfigureAwait(false);
    }
}

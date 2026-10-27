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
        => ResolvePermissionAsync(request, approve: true, approveForSession: false);

    [RelayCommand]
    private Task ApprovePermissionForSessionAsync(AgentPendingPermissionRequestRecord? request)
        => ResolvePermissionAsync(request, approve: true, approveForSession: true);

    [RelayCommand]
    private Task DenyPermissionAsync(AgentPendingPermissionRequestRecord? request)
        => ResolvePermissionAsync(request, approve: false, approveForSession: false);

    private Task ResolvePermissionAsync(
        AgentPendingPermissionRequestRecord? request,
        bool approve,
        bool approveForSession)
    {
        if (_disposed || SelectedSession is null
            || request is null
            || _permissionPanel.TryBeginDecision(request) is not { } decision)
        {
            return Task.CompletedTask;
        }

        // Reserve and dismiss on the click's UI turn, before dispatch or reconciliation can wait.
        NotifyPermissionPanelChanged();
        var runtimeInstanceId = _appliedRuntimeInstanceId;
        return RunOperationAsync(
            ResolvePermissionOperation,
            cancellationToken => ResolvePermissionCoreAsync(decision, approve, approveForSession, runtimeInstanceId, cancellationToken),
            cancellationToken => ReconcileSessionPermissionsAsync(decision.DisplaySessionId, cancellationToken,
                decision: decision, retryDecision: true));
    }

    private async Task ResolvePermissionCoreAsync(
        AgentPermissionPanelState.Decision decision,
        bool approve,
        bool approveForSession,
        string? runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = approve
            ? await _permissionPanel.ApproveAsync(decision.Request, approveForSession, cancellationToken).ConfigureAwait(false)
            : await _permissionPanel.DenyAsync(decision.Request, cancellationToken).ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() => _permissionPanel.AcceptDecision(decision)).ConfigureAwait(false);
        await ReconcileSessionPermissionsAsync(decision.DisplaySessionId, cancellationToken, decision: decision)
            .ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            if (_disposed || SelectedSession is not { } selectedSession
                || selectedSession.SessionId != decision.DisplaySessionId
                || !string.Equals(runtimeInstanceId, _appliedRuntimeInstanceId, StringComparison.Ordinal)) return;
            ApplySessionStatus(selectedSession, summary);
            NotifyPermissionPanelChanged();
            SyncSelectedSessionState(selectedSession.SessionId);
        }).ConfigureAwait(false);
    }

    private void ResetPermissionRuntime(string? runtimeInstanceId)
    {
        _appliedRuntimeInstanceId = runtimeInstanceId;
        _appliedPermissionRevision = 0;
        Interlocked.Increment(ref _permissionRequestGeneration);
        _permissionPanel.ResetDecisions();
    }
}

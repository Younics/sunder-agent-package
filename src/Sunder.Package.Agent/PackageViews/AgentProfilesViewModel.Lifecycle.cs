namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _runtimeAvailabilityCallbackVersion);
        _lifetimeCancellation.Cancel();
        _initialization.Dispose();
        _statusClear.Dispose();
        _runtimeNoticeDelay.Dispose();
        _tasks.Dispose();
        _operation.PropertyChanged -= OnOperationPropertyChanged;
        _operation.Dispose();
        _profileService.ProfileChanged -= OnProfileChanged;
        _profileService.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged -= OnRuntimeConnectionStateChanged;
        }
        ChatBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        EmbeddingBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        ChatBinding.Changed -= OnEditorSelectionChanged;
        EmbeddingBinding.Changed -= OnEditorSelectionChanged;
        Capabilities.Changed -= OnCapabilitiesChanged;
        _listDetail.SelectionChanging -= OnProfileSelectionChanging;
        _listDetail.PropertyChanged -= OnListDetailPropertyChanged;
        _runtimeRefresh.Dispose();
        _listDetail.Dispose();
        _requests.Dispose();
        ChatBinding.Dispose();
        EmbeddingBinding.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void OnRuntimeConnectionStateChanged(Runtime.AgentRuntimeConnectionState state)
    {
        var callbackVersion = Interlocked.Increment(ref _runtimeAvailabilityCallbackVersion);
        RunOnUiThread(() => ApplyRuntimeConnectionState(callbackVersion, state));
    }

    private void ApplyRuntimeConnectionState(
        long callbackVersion,
        Runtime.AgentRuntimeConnectionState state)
    {
        if (_disposed
            || callbackVersion != Volatile.Read(ref _runtimeAvailabilityCallbackVersion)
            || _runtimeAvailability?.ConnectionState != state)
        {
            return;
        }

        if (state == Runtime.AgentRuntimeConnectionState.Connected)
        {
            CancelRuntimeNotice();
            _tasks.Run(cancellationToken => _isInitialized
                ? _runtimeRefresh.MarkDirty().WaitAsync(cancellationToken)
                : InitializeAsync(cancellationToken));
        }
        else if (state is Runtime.AgentRuntimeConnectionState.Unavailable
                 or Runtime.AgentRuntimeConnectionState.Reconnecting)
        {
            ScheduleRuntimeNotice();
        }
        else
        {
            CancelRuntimeNotice();
        }
    }

    private void ReconcileRuntimeNoticeState()
    {
        if (_runtimeAvailability?.ConnectionState is Runtime.AgentRuntimeConnectionState.Unavailable
            or Runtime.AgentRuntimeConnectionState.Reconnecting)
        {
            ScheduleRuntimeNotice();
        }
        else
        {
            CancelRuntimeNotice();
        }
    }

    private void ScheduleRuntimeNotice()
    {
        if (_disposed || _runtimeNoticePending || HasRuntimeNotice)
        {
            return;
        }

        _runtimeNoticePending = true;
        _tasks.Run(_ => _runtimeNoticeDelay.ScheduleAsync(RuntimeNoticeGracePeriod, () =>
        {
            _runtimeNoticePending = false;
            if (_disposed
                || _runtimeAvailability?.ConnectionState is not (
                    Runtime.AgentRuntimeConnectionState.Unavailable
                    or Runtime.AgentRuntimeConnectionState.Reconnecting))
            {
                return;
            }

            RuntimeNoticeText = "Agent Runtime is unavailable. Reconnecting...";
        }));
    }

    private void CancelRuntimeNotice()
    {
        _runtimeNoticePending = false;
        _runtimeNoticeDelay.Cancel();
        RuntimeNoticeText = string.Empty;
    }
}

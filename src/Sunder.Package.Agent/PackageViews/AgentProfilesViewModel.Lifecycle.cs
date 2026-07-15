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
        _profileLoadVersion++;
        _lifetimeCancellation.Cancel();
        _initialization.Dispose();
        _statusClear.Dispose();
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
        ChatBinding.Dispose();
        EmbeddingBinding.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void OnRuntimeConnectionStateChanged(Runtime.AgentRuntimeConnectionState state)
    {
        if (_disposed)
        {
            return;
        }
        if (state == Runtime.AgentRuntimeConnectionState.Connected && _isInitialized)
        {
            RunOnUiThread(() => _tasks.Run(_ => ReloadProfilesSafelyAsync(SelectedProfile?.ProfileId)));
        }
        else if (state is Runtime.AgentRuntimeConnectionState.Unavailable
                 or Runtime.AgentRuntimeConnectionState.Reconnecting)
        {
            SetStatus("Agent Runtime is unavailable. Reconnecting...", AgentProfileStatusKind.Warning);
        }
    }
}

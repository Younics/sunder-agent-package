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
        _statusClear.Dispose();
        _profileService.ProfileChanged -= OnProfileChanged;
        _profileService.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
        ChatBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        EmbeddingBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        ChatBinding.Changed -= OnEditorSelectionChanged;
        EmbeddingBinding.Changed -= OnEditorSelectionChanged;
        Capabilities.Changed -= OnCapabilitiesChanged;
        ChatBinding.Dispose();
        EmbeddingBinding.Dispose();
        _lifetimeCancellation.Dispose();
    }
}

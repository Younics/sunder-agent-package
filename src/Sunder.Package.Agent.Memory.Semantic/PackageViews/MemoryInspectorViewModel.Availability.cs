using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel
{
    private IOwnerStreamAvailability? _ownerAvailability;

    [ObservableProperty]
    private bool _isRuntimeAvailable;

    private void InitializeOwnerAvailability(IMemoryInspectorGateway gateway)
    {
        _ownerAvailability = gateway as IOwnerStreamAvailability;
        IsRuntimeAvailable = _ownerAvailability?.IsOwnerStreamAvailable ?? true;
        if (_ownerAvailability is not null)
        {
            _ownerAvailability.OwnerStreamAvailabilityChanged += OnOwnerStreamAvailabilityChanged;
        }
    }

    private void DisposeOwnerAvailability()
    {
        if (_ownerAvailability is not null)
        {
            _ownerAvailability.OwnerStreamAvailabilityChanged -= OnOwnerStreamAvailabilityChanged;
        }
    }

    private void OnOwnerStreamAvailabilityChanged(bool available)
        => _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                IsRuntimeAvailable = available;
            }
        });
}

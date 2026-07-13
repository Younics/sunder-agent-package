namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        _pendingAnchor = null;
        _pendingSettledScrollCompleted = null;
        _pendingBottomPlacementReleaseCompleted = null;
        _setViewportAnchor?.Invoke(null);
        _lifetimeCancellation.Dispose();
    }
}

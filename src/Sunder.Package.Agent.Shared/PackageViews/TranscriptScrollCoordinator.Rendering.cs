using Avalonia;
using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (_disposed)
        {
            return;
        }

        if (change.Property == ScrollViewer.OffsetProperty)
        {
            OnScrollOffsetChanged();
            return;
        }

        if (change.Property == ScrollViewer.ExtentProperty || change.Property == ScrollViewer.ViewportProperty)
        {
            SignalViewportMutation(ViewportMutationSignalCause.ExtentChanged);
            if (_bottomPlacementLockActive)
            {
                if (_bottomPlacementInteractionRevision == _interactionRevision
                    && _bottomPlacementAuthorityRevision == _viewportAuthorityRevision)
                {
                    PinToBottom(TranscriptProgrammaticOffsetWriteSource.BottomPlacementExtentChanged);
                }
                else
                {
                    CancelBottomPlacementLockForUserInteraction();
                }
                UpdateJumpToLatestVisibility();
                return;
            }

            UpdateJumpToLatestVisibility();
        }
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        if (_disposed
            || !_captureViewportAnchorOnScrollChanged
            || _viewportAnchorCaptureInteractionRevision != _interactionRevision)
        {
            return;
        }

        _captureViewportAnchorOnScrollChanged = false;
        if (!_presentationActive
            || _isProgrammaticScroll
            || _isRestoringAnchor
            || _bottomPlacementLockActive)
        {
            return;
        }

        CaptureViewportAnchor();
    }

    public void OnRenderedContentChanged(ITranscriptGeometrySource? source = null)
    {
        if (_disposed || !_presentationActive)
        {
            return;
        }

        SignalViewportMutation(ViewportMutationSignalCause.RenderedContent, source);
        UpdateJumpToLatestVisibility();
    }
}

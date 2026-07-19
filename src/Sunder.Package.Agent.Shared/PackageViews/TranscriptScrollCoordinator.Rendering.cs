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
            if (_bottomPlacementLockActive)
            {
                if (_bottomPlacementInteractionRevision == _interactionRevision)
                {
                    PinToBottom();
                }
                else
                {
                    CancelBottomPlacementLockForUserInteraction();
                }
                UpdateJumpToLatestVisibility();
                return;
            }

            if (ShouldPinToBottomForLayoutGrowth())
            {
                if (_anchorHost is not null)
                {
                    PinToBottom();
                }
                else
                {
                    QueueScrollToBottom();
                }
                UpdateJumpToLatestVisibility();
                return;
            }

            UpdateJumpToLatestVisibility();
            if (_suppressEdgeLoadsUntilNextScroll
                || _isRestoringAnchor
                || _pendingAnchor is not null
                || _loadOlderPending
                || _loadNewerPending)
            {
                return;
            }

            QueueLoadOlderRowsIfNearTop();
            QueueLoadNewerRowsIfAtBottom(requireActualBottom: false);
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

    public void OnRenderedContentChanged()
    {
        if (_disposed || !_presentationActive)
        {
            return;
        }

        if (_isRestoringAnchor || _restoreAnchorPending)
        {
            if (_pendingAnchor is not null)
            {
                _renderedContentChangedDuringAnchorRestore = true;
            }
            return;
        }
        if (_pendingAnchor is not null)
        {
            QueueRestoreScrollAnchor();
            return;
        }

        if (IsFollowingTail)
        {
            if (_bottomPlacementLockActive)
            {
                PinToBottom();
            }
            else if (_anchorHost is not null)
            {
                PinToBottom();
            }
            else
            {
                QueueScrollToBottom();
            }
            return;
        }

        CaptureViewportAnchor();
    }

    private bool ShouldPinToBottomForLayoutGrowth()
        => _presentationActive
           && IsFollowingTail
           && _tailFollowInteractionRevision == _interactionRevision
           && !_hasNewerRows()
           && !_isRestoringAnchor
           && _pendingAnchor is null
           && !_loadOlderPending
           && !_loadNewerPending;
}

using Avalonia.Input;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    public void BeginInitialPlacement(bool followTail = true)
    {
        if (_disposed)
        {
            return;
        }

        InvalidatePendingScrollOperations();
        _anchorHost?.ReleaseTrailingCompensator();
        if (followTail)
        {
            PrepareToFollowTail();
        }
        else
        {
            _userDetached = true;
            _anchorHost?.SetFollowingTail(false);
        }
        _lastUserScrollDirection = UserScrollDirection.None;
        _pendingUserScrollDirection = UserScrollDirection.None;
        _userScrollPending = false;
        _isOlderEdgeArmed = true;
        _isNewerEdgeArmed = true;
        UpdateJumpToLatestVisibility();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_activeViewportMutation is { } activeMutation)
        {
            TerminalizeViewportMutation(activeMutation, TranscriptViewportMutationStatus.Disposed);
        }
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _presentationPagingCancellation.Cancel();
        _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
        _scrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnUserPointerWheelChanged);
        _scrollViewer.RemoveHandler(InputElement.PointerPressedEvent, OnUserPointerPressed);
        _scrollViewer.RemoveHandler(InputElement.PointerMovedEvent, OnUserPointerMoved);
        _scrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnUserPointerReleased);
        _scrollViewer.RemoveHandler(InputElement.KeyDownEvent, OnUserKeyDown);
        _scrollViewer.RemoveHandler(InputElement.GotFocusEvent, OnDescendantGotFocus);
        _scrollViewer.RemoveHandler(InputElement.ScrollGestureEvent, OnUserScrollGesture);
        _scrollViewer.RemoveHandler(InputElement.ScrollGestureEndedEvent, OnUserScrollGestureEnded);
        _bottomPlacementLockActive = false;
        ReleaseSettledExactAnchorLease();
        _anchorHost?.ReleaseTrailingCompensator();
        _anchorHost?.SetFollowingTail(IsFollowingTail);
        ReleaseManualAnchoringSuspensions();
        _pendingSettledScrollCompleted = null;
        _pendingBottomPlacementReleaseCompleted = null;
        _setViewportAnchor?.Invoke(null);
        _presentationPagingCancellation.Dispose();
        _lifetimeCancellation.Dispose();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private long _interactionRevision;
    private long _bottomPlacementInteractionRevision;
    private long _forceScrollToBottomInteractionRevision;
    private Point? _touchScrollStart;
    private ScrollViewer? _touchNestedScrollViewer;
    private bool _touchScrollRecognized;
    private bool _scrollGestureActive;
    private bool _userScrollPending;
    private bool _captureViewportAnchorOnScrollChanged;
    private bool _pendingUserScrollCanResumeFollowing;
    private bool _scrollBarInteractionActive;
    private double _lastObservedOffsetY;
    private long _viewportAnchorCaptureInteractionRevision;
    private UserScrollDirection _pendingUserScrollDirection;
    private UserScrollDirection _lastUserScrollDirection;
    private Task _focusBringIntoViewOperation = Task.CompletedTask;

    private bool IsFollowingTail => !_userDetached && _isFollowingLatest();

    private void OnUserScrollInput(
        UserScrollDirection direction,
        bool canResumeFollowing = true)
    {
        ClaimViewportAuthorityForManualInteraction(
            preserveNewerPaging: direction == UserScrollDirection.TowardTail && _loadNewerPending);
        _userScrollPending = true;
        _pendingUserScrollCanResumeFollowing = canResumeFollowing;
        _pendingUserScrollDirection = direction;
        if (direction == UserScrollDirection.TowardHistory)
        {
            DetachFromLatestForUser();
        }

        _anchorHost?.SetFollowingTail(IsFollowingTail);
        QueuePendingUserScrollEvaluation(_interactionRevision);
    }

    private void ClaimViewportAuthorityForManualInteraction(
        bool preserveNewerPaging = false,
        bool cancelPendingPaging = false)
    {
        if (cancelPendingPaging)
        {
            CancelPendingPagingForViewportAuthority();
        }
        SupersedeViewportMutationForAuthority();
        _anchorHost?.ReleaseTrailingCompensator();
        _interactionRevision++;
        _loadNewerResumeInteractionRevision = -1;
        _pendingScrollToBottomRequest = null;
        _scrollToBottomPending = false;
        _forceScrollToBottomOnNextTranscriptChanged = false;
        _captureViewportAnchorOnScrollChanged = false;
        _userScrollPending = false;
        _pendingUserScrollCanResumeFollowing = false;
        _pendingUserScrollDirection = UserScrollDirection.None;
        ClearPendingAnchor();
        ReleaseOlderPagingAnchor();
        if (preserveNewerPaging)
        {
            _newerPagingAnchor?.TransferAuthority(_viewportAuthorityRevision);
        }
        else
        {
            ReleaseNewerPagingAnchor();
        }
        CancelBottomPlacementLockForUserInteraction();
    }

    private void OnUserPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (Math.Abs(eventArgs.Delta.Y) <= 0.001)
        {
            return;
        }

        if (CanNestedScrollViewerConsume(eventArgs, eventArgs.Delta.Y))
        {
            return;
        }

        OnUserScrollInput(eventArgs.Delta.Y > 0
            ? UserScrollDirection.TowardHistory
            : UserScrollDirection.TowardTail);
    }

    private void OnUserScrollGesture(object? sender, ScrollGestureEventArgs eventArgs)
    {
        if (Math.Abs(eventArgs.Delta.Y) <= 0.001
            || CanNestedScrollViewerConsume(eventArgs, -eventArgs.Delta.Y))
        {
            return;
        }

        _scrollGestureActive = true;
        OnUserScrollInput(UserScrollDirection.None);
    }

    private void OnUserScrollGestureEnded(object? sender, ScrollGestureEndedEventArgs eventArgs)
    {
        if (!_scrollGestureActive)
        {
            return;
        }

        _scrollGestureActive = false;
        RequestViewportAnchorCapture();
    }

    private void OnUserPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (FindScrollBarOwner(eventArgs) is { } scrollBarOwner)
        {
            if (ReferenceEquals(scrollBarOwner, _scrollViewer))
            {
                _scrollBarInteractionActive = true;
                OnUserScrollInput(UserScrollDirection.None);
            }
            return;
        }

        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            _touchScrollStart = eventArgs.GetPosition(_scrollViewer);
            _touchNestedScrollViewer = FindNestedScrollViewer(eventArgs);
            _touchScrollRecognized = false;
            return;
        }
    }

    private void OnUserKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (IsControlActivationOrEditingKey(eventArgs))
        {
            return;
        }

        if (eventArgs.Key is Key.Up
            or Key.Down
            or Key.PageUp
            or Key.PageDown
            or Key.Home
            or Key.End
            or Key.Space)
        {
            var movesUp = eventArgs.Key is Key.Up or Key.PageUp or Key.Home
                || eventArgs.Key == Key.Space && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift);
            OnUserScrollInput(movesUp
                ? UserScrollDirection.TowardHistory
                : UserScrollDirection.TowardTail);
        }
    }

    private void OnUserPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (FindScrollBarOwner(eventArgs) is { } scrollBarOwner)
        {
            var properties = eventArgs.GetCurrentPoint(_scrollViewer).Properties;
            if (ReferenceEquals(scrollBarOwner, _scrollViewer) && properties.IsLeftButtonPressed)
            {
                _scrollBarInteractionActive = true;
                OnUserScrollInput(UserScrollDirection.None);
            }
            return;
        }

        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            if (_touchScrollRecognized || _touchScrollStart is not { } start)
            {
                return;
            }

            var delta = eventArgs.GetPosition(_scrollViewer) - start;
            if (Math.Abs(delta.Y) < 4)
            {
                return;
            }

            if (_touchNestedScrollViewer is { } nestedViewer
                && CanScrollViewerConsume(nestedViewer, delta.Y))
            {
                return;
            }

            _touchScrollRecognized = true;
            OnUserScrollInput(delta.Y > 0
                ? UserScrollDirection.TowardHistory
                : UserScrollDirection.TowardTail);
            return;
        }
    }

    private void OnUserPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            _touchScrollStart = null;
            _touchNestedScrollViewer = null;
            _touchScrollRecognized = false;
        }

        _scrollBarInteractionActive = false;
    }

    private void OnDescendantGotFocus(object? sender, FocusChangedEventArgs eventArgs)
    {
        if (_disposed
            || eventArgs.NavigationMethod == NavigationMethod.Pointer
            || eventArgs.Source is not Control control
            || !control.GetVisualAncestors().Contains(_scrollViewer))
        {
            return;
        }

        CancelPendingPagingForViewportAuthority();
        SupersedeViewportMutationForAuthority();
        _anchorHost?.ReleaseTrailingCompensator();
        var interactionRevision = _interactionRevision;
        var authorityRevision = _viewportAuthorityRevision;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (!_disposed
                    && interactionRevision == _interactionRevision
                    && authorityRevision == _viewportAuthorityRevision
                    && control.IsAttachedToVisualTree()
                    && control.IsFocused)
                {
                    var previousOffset = _scrollViewer.Offset.Y;
                    control.BringIntoView();
                    var offsetDelta = _scrollViewer.Offset.Y - previousOffset;
                    if (Math.Abs(offsetDelta) > 0.01)
                    {
                        OnUserScrollInput(
                            offsetDelta < 0
                                ? UserScrollDirection.TowardHistory
                                : UserScrollDirection.TowardTail,
                            canResumeFollowing: false);
                        RequestViewportAnchorCapture();
                    }
                }
            },
            DispatcherPriority.Input,
            _lifetimeCancellation.Token);
        _focusBringIntoViewOperation = ObservePagingOperationAsync(
            AwaitDispatcherOperationAsync(operation));
    }

    private static async Task AwaitDispatcherOperationAsync(DispatcherOperation operation)
        => await operation;

    private static ScrollViewer? FindScrollBarOwner(RoutedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Visual source)
        {
            return null;
        }

        var scrollBar = source as ScrollBar
                        ?? source.GetVisualAncestors().OfType<ScrollBar>().FirstOrDefault();
        return scrollBar?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
    }

    private static bool IsControlActivationOrEditingKey(KeyEventArgs eventArgs)
    {
        if (eventArgs.Source is not Visual source)
        {
            return false;
        }

        var sourceAndAncestors = source.GetVisualAncestors().Prepend(source);
        return sourceAndAncestors.Any(control => control is TextBox)
               || eventArgs.Key == Key.Space
               && sourceAndAncestors.Any(control => control is Button or ToggleButton);
    }

    private bool HasActiveTextSelection()
        => _scrollViewer.GetVisualDescendants()
               .OfType<MarkdownRenderer>()
               .Any(renderer => renderer.CanCopy)
           || _scrollViewer.GetVisualDescendants()
               .OfType<SelectableTextBlock>()
               .Any(block => !string.IsNullOrEmpty(block.SelectedText));

    private bool CanNestedScrollViewerConsume(RoutedEventArgs eventArgs, double scrollDeltaY)
        => FindNestedScrollViewer(eventArgs) is { } nestedViewer
           && CanScrollViewerConsume(nestedViewer, scrollDeltaY);

    private ScrollViewer? FindNestedScrollViewer(RoutedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Visual source)
        {
            return null;
        }

        var nestedViewer = (source as ScrollViewer ?? source.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault());
        return nestedViewer is null || ReferenceEquals(nestedViewer, _scrollViewer)
            ? null
            : nestedViewer;
    }

    internal static bool CanScrollViewerConsume(ScrollViewer scrollViewer, double scrollDeltaY)
    {
        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        return scrollDeltaY > 0
            ? scrollViewer.Offset.Y > TrueBottomEpsilon
            : scrollViewer.Offset.Y < maxOffsetY - TrueBottomEpsilon;
    }

    private UserScrollResolution ResolveUserScrollDirection(double offsetDelta)
    {
        var isUserScroll = _userScrollPending
                           || _scrollBarInteractionActive
                           || _touchScrollRecognized
                           || _scrollGestureActive;
        var hintedDirection = _pendingUserScrollDirection;
        var canResumeFollowing = _pendingUserScrollCanResumeFollowing
                                  || _scrollBarInteractionActive
                                  || _touchScrollRecognized
                                  || _scrollGestureActive;
        _userScrollPending = false;
        _pendingUserScrollCanResumeFollowing = false;
        _pendingUserScrollDirection = UserScrollDirection.None;
        if (!isUserScroll)
        {
            return new UserScrollResolution(UserScrollDirection.None, false);
        }

        if (offsetDelta < -0.01)
        {
            return new UserScrollResolution(UserScrollDirection.TowardHistory, canResumeFollowing);
        }

        if (offsetDelta > 0.01)
        {
            return new UserScrollResolution(UserScrollDirection.TowardTail, canResumeFollowing);
        }

        return new UserScrollResolution(hintedDirection, canResumeFollowing);
    }

    private void QueuePendingUserScrollEvaluation(long interactionRevision)
    {
        var operation = Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (_disposed
                    || interactionRevision != _interactionRevision
                    || !_userScrollPending)
                {
                    return;
                }

                var direction = _pendingUserScrollDirection;
                var canResumeFollowing = _pendingUserScrollCanResumeFollowing;
                _userScrollPending = false;
                _pendingUserScrollCanResumeFollowing = false;
                _pendingUserScrollDirection = UserScrollDirection.None;
                RefreshActivePageProtectedAnchorKey();
                if (direction == UserScrollDirection.TowardHistory)
                {
                    _lastUserScrollDirection = direction;
                    _isOlderEdgeArmed = true;
                    QueueLoadOlderRowsIfNearTop();
                    UpdateJumpToLatestVisibility();
                    return;
                }

                if (direction != UserScrollDirection.TowardTail || !canResumeFollowing)
                {
                    return;
                }

                _lastUserScrollDirection = direction;
                if (_hasNewerRows())
                {
                    QueueLoadNewerRows(resumeFollowingWhenCaughtUp: IsAtBottom());
                }
                else
                {
                    TryResumeFollowingAtBottom();
                }

                UpdateJumpToLatestVisibility();
            },
            DispatcherPriority.Input);
        _userScrollEvaluationOperation = ObservePagingOperationAsync(
            AwaitDispatcherOperationAsync(operation));
    }

    private bool DetachFromLatestForUser()
    {
        if (_userDetached)
        {
            return true;
        }

        var wasFollowingTail = IsFollowingTail;
        _userDetached = true;
        _anchorHost?.SetFollowingTail(false);
        if (wasFollowingTail
            && _onDetachedFromLatest is not null
            && !_onDetachedFromLatest())
        {
            return false;
        }

        return true;
    }

    private void PrepareToFollowTail()
    {
        _anchorHost?.ReleaseTrailingCompensator();
        _userDetached = false;
        _anchorHost?.SetFollowingTail(IsFollowingTail);
        ClearPendingAnchor();
    }

    private bool ResumeFollowingLatest()
    {
        _anchorHost?.ReleaseTrailingCompensator();
        var wasUserDetached = _userDetached;
        _userDetached = false;
        if (!_isFollowingLatest()
            && _onReachedLatest is not null
            && !_onReachedLatest())
        {
            _userDetached = wasUserDetached;
            _anchorHost?.SetFollowingTail(IsFollowingTail);
            return false;
        }

        _anchorHost?.SetFollowingTail(true);
        return true;
    }

    private void RequestViewportAnchorCapture()
    {
        _captureViewportAnchorOnScrollChanged = true;
        _viewportAnchorCaptureInteractionRevision = _interactionRevision;
    }

    private void InvalidatePendingScrollOperations()
    {
        CancelPendingPagingForViewportAuthority();
        SupersedeViewportMutationForAuthority();
        _anchorHost?.ReleaseTrailingCompensator();
        _interactionRevision++;
        _pagingContextRevision++;
        _loadNewerResumeInteractionRevision = -1;
        _pendingScrollToBottomRequest = null;
        _captureViewportAnchorOnScrollChanged = false;
        _anchorHost?.SetFollowingTail(IsFollowingTail);
        ClearPendingAnchor();
        ReleaseOlderPagingAnchor();
        ReleaseNewerPagingAnchor();
        CancelBottomPlacementLockForUserInteraction();
    }

    private void CancelPendingPagingForViewportAuthority()
    {
        if (!_loadOlderPending && !_loadNewerPending)
        {
            return;
        }

        var superseded = _presentationPagingCancellation;
        _presentationPagingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _pagingContextRevision++;
        superseded.Cancel();
        superseded.Dispose();
    }

    private void CancelBottomPlacementLockForUserInteraction()
        => CancelBottomPlacementLock(invokeCompletion: true);

    private void CancelBottomPlacementLock(bool invokeCompletion)
    {
        if (!_bottomPlacementLockActive)
        {
            return;
        }

        _bottomPlacementLockVersion++;
        _bottomPlacementLockActive = false;
        var callback = _pendingBottomPlacementReleaseCompleted ?? _pendingSettledScrollCompleted;
        _pendingBottomPlacementReleaseCompleted = null;
        _pendingSettledScrollCompleted = null;
        _anchorHost?.SetFollowingTail(IsFollowingTail);
        ReleaseBottomPlacementAnchoringSuspension();
        UpdateJumpToLatestVisibility();
        if (invokeCompletion)
        {
            callback?.Invoke();
        }
    }

    private enum UserScrollDirection
    {
        None,
        TowardHistory,
        TowardTail,
    }

    private readonly record struct UserScrollResolution(
        UserScrollDirection Direction,
        bool CanResumeFollowing);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private long _interactionRevision;
    private long _tailFollowInteractionRevision;
    private long _bottomPlacementInteractionRevision;
    private long _scrollToBottomInteractionRevision;
    private long _forceScrollToBottomInteractionRevision;
    private Point? _touchScrollStart;
    private bool _touchScrollRecognized;
    private Task _focusBringIntoViewOperation = Task.CompletedTask;

    private void OnUserScrollInput(bool detachFromLatest)
    {
        _interactionRevision++;
        _pendingAnchor = null;
        if (detachFromLatest)
        {
            SetShouldAutoScroll(false);
            _onDetachedFromLatest?.Invoke();
        }
        else if (_shouldAutoScroll)
        {
            _tailFollowInteractionRevision = _interactionRevision;
        }

        CancelBottomPlacementLockForUserInteraction();
    }

    private void OnUserPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
        => OnUserScrollInput(detachFromLatest: eventArgs.Delta.Y > 0);

    private void OnUserPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            _touchScrollStart = eventArgs.GetPosition(_scrollViewer);
            _touchScrollRecognized = false;
            return;
        }

        if (IsScrollBarInput(eventArgs))
        {
            OnUserScrollInput(detachFromLatest: true);
        }
    }

    private void OnUserKeyDown(object? sender, KeyEventArgs eventArgs)
    {
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
            OnUserScrollInput(detachFromLatest: movesUp);
        }
    }

    private void OnUserPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
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

            _touchScrollRecognized = true;
            OnUserScrollInput(detachFromLatest: delta.Y > 0);
            return;
        }

        var properties = eventArgs.GetCurrentPoint(_scrollViewer).Properties;
        if (properties.IsLeftButtonPressed && IsScrollBarInput(eventArgs))
        {
            OnUserScrollInput(detachFromLatest: true);
        }
    }

    private void OnUserPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            _touchScrollStart = null;
            _touchScrollRecognized = false;
        }
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

        var operation = Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (!_disposed && control.IsAttachedToVisualTree())
                {
                    control.BringIntoView();
                }
            },
            DispatcherPriority.Input,
            _lifetimeCancellation.Token);
        _focusBringIntoViewOperation = ObservePagingOperationAsync(
            AwaitDispatcherOperationAsync(operation));
    }

    private static async Task AwaitDispatcherOperationAsync(DispatcherOperation operation)
        => await operation;

    private static bool IsScrollBarInput(RoutedEventArgs eventArgs)
        => eventArgs.Source is Visual source
            && (source is ScrollBar || source.GetVisualAncestors().OfType<ScrollBar>().Any());

    private void SetShouldAutoScroll(bool value)
    {
        _shouldAutoScroll = value;
        if (value)
        {
            _tailFollowInteractionRevision = _interactionRevision;
        }
    }

    private void CancelBottomPlacementLockForUserInteraction()
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
        UpdateJumpToLatestVisibility();
        callback?.Invoke();
    }
}

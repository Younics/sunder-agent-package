using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator : IDisposable
{
    private const double DefaultAutoScrollThreshold = 24;
    private const double DefaultLoadOlderThreshold = 96;
    private const double DefaultLoadNewerThreshold = 96;
    private const double ViewportLoadThresholdRatio = 0.25;
    private const int BottomPlacementMaxRenderPasses = 8;
    private const int BottomPlacementStableRenderPasses = 2;

    private readonly ScrollViewer _scrollViewer;
    private readonly Control? _itemsControl;
    private readonly Func<bool> _canLoadOlderRows;
    private readonly Func<object?, CancellationToken, Task<bool>> _loadOlderRowsAsync;
    private readonly Func<bool> _canLoadNewerRows;
    private readonly Func<object?, CancellationToken, Task<bool>> _loadNewerRowsAsync;
    private readonly Func<bool> _hasNewerRows;
    private readonly Action<bool>? _setJumpToLatestVisible;
    private readonly Action? _onDetachedFromLatest;
    private readonly Action? _onReachedLatest;
    private readonly Action<TranscriptViewportAnchorData?>? _setViewportAnchor;
    private readonly Action<Exception>? _pagingFailed;
    private readonly Func<IEnumerable<(object Item, Control Visual)>>? _enumerateRealizedAnchors;
    private readonly Func<object, Control?>? _realizeAnchor;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly double _autoScrollThreshold;
    private readonly double _loadOlderThreshold;
    private readonly double _loadNewerThreshold;
    private bool _shouldAutoScroll = true;
    private bool _forceScrollToBottomOnNextTranscriptChanged;
    private bool _isJumpToLatestVisible;
    private bool _isProgrammaticScroll;
    private bool _isRestoringAnchor;
    private bool _scrollToBottomPending;
    private bool _bottomPlacementLockActive;
    private bool _restoreAnchorPending;
    private bool _loadOlderPending;
    private bool _loadNewerPending;
    private bool _suppressEdgeLoadsUntilNextScroll;
    private bool _isOlderEdgeArmed = true;
    private bool _isNewerEdgeArmed = true;
    private int _bottomPlacementLockVersion;
    private Action? _pendingSettledScrollCompleted;
    private Action? _pendingBottomPlacementReleaseCompleted;
    private ScrollAnchor? _pendingAnchor;
    private Task _loadOlderOperation = Task.CompletedTask;
    private Task _loadNewerOperation = Task.CompletedTask;
    private Task _settledScrollOperation = Task.CompletedTask;
    private Task _bottomPlacementReleaseOperation = Task.CompletedTask;
    private Task _restoreAnchorOperation = Task.CompletedTask;
    private Task _scrollToBottomOperation = Task.CompletedTask;
    private bool _disposed;

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        Control? itemsControl,
        Func<bool> canLoadOlderRows,
        Func<object?, CancellationToken, Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<object?, CancellationToken, Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Action<bool>? setJumpToLatestVisible = null,
        Action? onDetachedFromLatest = null,
        Action? onReachedLatest = null,
        Action<TranscriptViewportAnchorData?>? setViewportAnchor = null,
        Action<Exception>? pagingFailed = null,
        Func<IEnumerable<(object Item, Control Visual)>>? enumerateRealizedAnchors = null,
        Func<object, Control?>? realizeAnchor = null,
        double autoScrollThreshold = DefaultAutoScrollThreshold,
        double loadOlderThreshold = DefaultLoadOlderThreshold,
        double loadNewerThreshold = DefaultLoadNewerThreshold)
    {
        _scrollViewer = scrollViewer;
        _itemsControl = itemsControl;
        _canLoadOlderRows = canLoadOlderRows;
        _loadOlderRowsAsync = loadOlderRowsAsync;
        _canLoadNewerRows = canLoadNewerRows;
        _loadNewerRowsAsync = loadNewerRowsAsync;
        _hasNewerRows = hasNewerRows;
        _setJumpToLatestVisible = setJumpToLatestVisible;
        _onDetachedFromLatest = onDetachedFromLatest;
        _onReachedLatest = onReachedLatest;
        _setViewportAnchor = setViewportAnchor;
        _pagingFailed = pagingFailed;
        _enumerateRealizedAnchors = enumerateRealizedAnchors;
        _realizeAnchor = realizeAnchor;
        _autoScrollThreshold = autoScrollThreshold;
        _loadOlderThreshold = loadOlderThreshold;
        _loadNewerThreshold = loadNewerThreshold;
        _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        _scrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnUserPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _scrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            OnUserPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _scrollViewer.AddHandler(
            InputElement.PointerMovedEvent,
            OnUserPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _scrollViewer.AddHandler(
            InputElement.PointerReleasedEvent,
            OnUserPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _scrollViewer.AddHandler(
            InputElement.KeyDownEvent,
            OnUserKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _scrollViewer.AddHandler(
            InputElement.GotFocusEvent,
            OnDescendantGotFocus,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        UpdateJumpToLatestVisibility();
    }

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        Func<bool> canLoadOlderRows,
        Func<object?, CancellationToken, Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<object?, CancellationToken, Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Action<bool>? setJumpToLatestVisible = null,
        Action? onDetachedFromLatest = null,
        Action? onReachedLatest = null,
        Action<TranscriptViewportAnchorData?>? setViewportAnchor = null,
        Action<Exception>? pagingFailed = null,
        Func<IEnumerable<(object Item, Control Visual)>>? enumerateRealizedAnchors = null,
        Func<object, Control?>? realizeAnchor = null,
        double autoScrollThreshold = DefaultAutoScrollThreshold,
        double loadOlderThreshold = DefaultLoadOlderThreshold,
        double loadNewerThreshold = DefaultLoadNewerThreshold)
        : this(
            scrollViewer,
            null,
            canLoadOlderRows,
            loadOlderRowsAsync,
            canLoadNewerRows,
            loadNewerRowsAsync,
            hasNewerRows,
            setJumpToLatestVisible,
            onDetachedFromLatest,
            onReachedLatest,
            setViewportAnchor,
            pagingFailed,
            enumerateRealizedAnchors,
            realizeAnchor,
            autoScrollThreshold,
            loadOlderThreshold,
            loadNewerThreshold)
    {
    }

    public void BeginTranscriptMutation()
    {
        if (_disposed)
        {
            return;
        }

        _pendingAnchor ??= CaptureScrollAnchor(ScrollAnchorMode.LiveTranscriptMutation);
    }

    public void BeginViewportMutation()
    {
        if (_disposed)
        {
            return;
        }

        _pendingAnchor ??= CaptureScrollAnchor(ScrollAnchorMode.ViewportMutation);
    }

    public void OnViewportContentChanged()
    {
        UpdateJumpToLatestVisibility();
        if (_pendingAnchor is not null)
        {
            QueueRestoreScrollAnchor();
        }
    }

    public void DiscardPendingTranscriptMutation()
    {
        _pendingAnchor = null;
    }

    public void OnTranscriptChanged()
    {
        UpdateJumpToLatestVisibility();
        if (_forceScrollToBottomOnNextTranscriptChanged)
        {
            _forceScrollToBottomOnNextTranscriptChanged = false;
            _pendingAnchor = null;
            if (_forceScrollToBottomInteractionRevision == _interactionRevision)
            {
                QueueScrollToBottom(force: true);
            }
            return;
        }

        if (_pendingAnchor is not null)
        {
            if (_loadOlderPending || _loadNewerPending)
            {
                _pendingAnchor = null;
                return;
            }

            QueueRestoreScrollAnchor();
            return;
        }

        if (_loadOlderPending || _loadNewerPending)
        {
            return;
        }

        if (QueueLoadNewerRowsIfAtBottom(requireActualBottom: true))
        {
            return;
        }

        if (_shouldAutoScroll && !_hasNewerRows())
        {
            QueueScrollToBottom();
        }
    }

    public void ForceScrollToBottomOnNextTranscriptChanged()
    {
        _forceScrollToBottomOnNextTranscriptChanged = true;
        _forceScrollToBottomInteractionRevision = _interactionRevision;
        SetShouldAutoScroll(true);
        _pendingAnchor = null;
    }

    public void QueueScrollToBottom(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        if (force)
        {
            _pendingAnchor = null;
            SetShouldAutoScroll(true);
        }

        _scrollToBottomInteractionRevision = _interactionRevision;
        if (_scrollToBottomPending)
        {
            return;
        }

        _scrollToBottomPending = true;
        var operation = InvokeOnDispatcherAsync(() =>
        {
            _scrollToBottomPending = false;
            if (_scrollToBottomInteractionRevision != _interactionRevision)
            {
                return;
            }

            ScrollToBottom();
        }, DispatcherPriority.Render);
        _scrollToBottomOperation = ObservePagingOperationAsync(operation);
    }

    private static async Task InvokeOnDispatcherAsync(Action action, DispatcherPriority priority)
        => await Dispatcher.UIThread.InvokeAsync(action, priority);

    public void QueueScrollToBottomAfterLayoutSettles(
        Action? completed = null,
        CancellationToken cancellationToken = default)
    {
        var version = BeginBottomPlacementLock();
        _pendingSettledScrollCompleted = completed;
        var interactionRevision = _bottomPlacementInteractionRevision;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => CompleteSettledScrollAsync(version, interactionRevision, cancellationToken),
            DispatcherPriority.Loaded);
        _settledScrollOperation = ObservePagingOperationAsync(operation);
    }

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
                PinToBottom();
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

    private bool ShouldPinToBottomForLayoutGrowth()
        => _shouldAutoScroll
           && _tailFollowInteractionRevision == _interactionRevision
           && !_hasNewerRows()
           && !_isRestoringAnchor
           && _pendingAnchor is null
           && !_loadOlderPending
           && !_loadNewerPending;

    private void OnScrollOffsetChanged()
    {
        if (_isProgrammaticScroll)
        {
            return;
        }

        if (_bottomPlacementLockActive)
        {
            UpdateJumpToLatestVisibility();
            return;
        }

        _suppressEdgeLoadsUntilNextScroll = false;

        if (_isRestoringAnchor)
        {
            UpdateJumpToLatestVisibility();
            return;
        }

        var isNearBottom = IsNearBottom();
        var isNearTop = IsNearLoadTop();
        var isNearLoadBottom = IsNearLoadBottom();
        if (!isNearTop)
        {
            _isOlderEdgeArmed = true;
        }

        if (!isNearLoadBottom)
        {
            _isNewerEdgeArmed = true;
        }

        SetShouldAutoScroll(isNearBottom && !_hasNewerRows());
        if (!isNearBottom)
        {
            _onDetachedFromLatest?.Invoke();
        }

        if (_loadOlderPending || _loadNewerPending)
        {
            UpdateJumpToLatestVisibility();
            return;
        }

        var queuedOlderLoad = false;
        if (isNearTop)
        {
            queuedOlderLoad = QueueLoadOlderRows();
        }

        if (!queuedOlderLoad && isNearLoadBottom)
        {
            QueueLoadNewerRows();
            NotifyReachedLatestIfCaughtUp();
        }

        UpdateJumpToLatestVisibility();
    }

    internal bool QueueLoadOlderRows()
    {
        if (_disposed)
        {
            return false;
        }

        if (_loadOlderPending)
        {
            return true;
        }

        if (!_isOlderEdgeArmed)
        {
            return false;
        }

        if (_loadNewerPending || _isRestoringAnchor || _restoreAnchorPending || !_canLoadOlderRows())
        {
            return false;
        }

        _isOlderEdgeArmed = false;
        var anchor = CaptureScrollAnchor(ScrollAnchorMode.OlderRowsMutation);
        var interactionRevision = anchor.InteractionRevision;
        _loadOlderPending = true;

        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadOlderRowsAsync(anchor, interactionRevision, _lifetimeCancellation.Token),
            DispatcherPriority.Background);
        _loadOlderOperation = ObservePagingOperationAsync(operation);

        return true;
    }

    internal bool QueueLoadNewerRows()
    {
        if (_disposed)
        {
            return false;
        }

        if (_loadNewerPending)
        {
            return true;
        }

        if (!_isNewerEdgeArmed)
        {
            return false;
        }

        if (_loadOlderPending || _isRestoringAnchor || _restoreAnchorPending || !_canLoadNewerRows())
        {
            return false;
        }

        _isNewerEdgeArmed = false;
        var anchor = CaptureScrollAnchor(ScrollAnchorMode.ViewportMutation);
        var wasAtBottom = IsNearBottom();
        var interactionRevision = anchor.InteractionRevision;
        _loadNewerPending = true;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadNewerRowsAsync(
                anchor,
                wasAtBottom,
                interactionRevision,
                _lifetimeCancellation.Token),
            DispatcherPriority.Background);
        _loadNewerOperation = ObservePagingOperationAsync(operation);

        return true;
    }

    private bool QueueLoadOlderRowsIfNearTop()
    {
        if (_shouldAutoScroll || !_isOlderEdgeArmed || !IsNearLoadTop())
        {
            return false;
        }

        return QueueLoadOlderRows();
    }

    private bool QueueLoadNewerRowsIfAtBottom(bool requireActualBottom)
    {
        if (!_hasNewerRows())
        {
            NotifyReachedLatestIfCaughtUp();
            return false;
        }

        if (requireActualBottom ? !IsNearBottom() : !IsNearLoadBottom())
        {
            return false;
        }

        if (!_isNewerEdgeArmed)
        {
            return false;
        }

        if (_loadNewerPending)
        {
            return true;
        }

        if (!_canLoadNewerRows())
        {
            return false;
        }

        return QueueLoadNewerRows();
    }

    private void ScrollToBottom()
    {
        PinToBottom();
        SetShouldAutoScroll(!_hasNewerRows());
        if (!_hasNewerRows())
        {
            _onReachedLatest?.Invoke();
        }

        UpdateJumpToLatestVisibility();
    }

    private void NotifyReachedLatestIfCaughtUp()
    {
        if (!_hasNewerRows() && IsNearBottom())
        {
            _onReachedLatest?.Invoke();
        }
    }

    private async Task CompleteSettledScrollAsync(
        int version,
        long interactionRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            await ScrollToBottomAfterLayoutSettlesAsync(
                interactionRevision,
                cancellationToken);
        }
        finally
        {
            if (version == _bottomPlacementLockVersion)
            {
                if (cancellationToken.IsCancellationRequested || _disposed)
                {
                    _pendingSettledScrollCompleted = null;
                    _bottomPlacementLockActive = false;
                }
                else
                {
                    var completed = _pendingSettledScrollCompleted;
                    _pendingSettledScrollCompleted = null;
                    QueueReleaseBottomPlacementLock(completed, cancellationToken);
                }
            }
        }
    }

    private async Task ScrollToBottomAfterLayoutSettlesAsync(
        long interactionRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (interactionRevision != _interactionRevision)
        {
            return;
        }

        _pendingAnchor = null;
        SetShouldAutoScroll(true);

        var previousExtentHeight = -1d;
        var previousViewportHeight = -1d;
        var stablePasses = 0;
        for (var pass = 0; pass < BottomPlacementMaxRenderPasses; pass++)
        {
            await YieldForRenderedContent(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || interactionRevision != _interactionRevision)
            {
                return;
            }
            PinToBottom();

            var extentHeight = _scrollViewer.Extent.Height;
            var viewportHeight = _scrollViewer.Viewport.Height;
            if (viewportHeight > 0
                && Math.Abs(extentHeight - previousExtentHeight) < 0.5
                && Math.Abs(viewportHeight - previousViewportHeight) < 0.5
                && IsNearBottom())
            {
                stablePasses++;
            }
            else
            {
                stablePasses = 0;
            }

            if (stablePasses >= BottomPlacementStableRenderPasses)
            {
                break;
            }

            previousExtentHeight = extentHeight;
            previousViewportHeight = viewportHeight;
        }

        if (interactionRevision == _interactionRevision)
        {
            ScrollToBottom();
        }
    }

    private int BeginBottomPlacementLock()
    {
        var supersededSettledCallback = _pendingSettledScrollCompleted;
        var supersededReleaseCallback = _pendingBottomPlacementReleaseCompleted;
        _pendingSettledScrollCompleted = null;
        _pendingBottomPlacementReleaseCompleted = null;
        _bottomPlacementLockVersion++;
        _bottomPlacementLockActive = true;
        _bottomPlacementInteractionRevision = _interactionRevision;
        _pendingAnchor = null;
        SetShouldAutoScroll(true);
        UpdateJumpToLatestVisibility();
        supersededSettledCallback?.Invoke();
        if (!ReferenceEquals(supersededReleaseCallback, supersededSettledCallback))
        {
            supersededReleaseCallback?.Invoke();
        }
        return _bottomPlacementLockVersion;
    }

    private void QueueReleaseBottomPlacementLock(
        Action? completed = null,
        CancellationToken cancellationToken = default)
    {
        _pendingBottomPlacementReleaseCompleted = completed;
        var version = _bottomPlacementLockVersion;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => ReleaseBottomPlacementLockAsync(version, cancellationToken),
            DispatcherPriority.Background);
        _bottomPlacementReleaseOperation = ObservePagingOperationAsync(operation);
    }

    private async Task ReleaseBottomPlacementLockAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            await YieldForRenderedContent(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_disposed
                && version == _bottomPlacementLockVersion
                && _bottomPlacementInteractionRevision == _interactionRevision)
            {
                PinToBottom();
            }
        }
        finally
        {
            if (version == _bottomPlacementLockVersion)
            {
                if (cancellationToken.IsCancellationRequested || _disposed)
                {
                    _pendingBottomPlacementReleaseCompleted = null;
                    _bottomPlacementLockActive = false;
                }
                else
                {
                    if (_bottomPlacementInteractionRevision != _interactionRevision)
                    {
                        CancelBottomPlacementLockForUserInteraction();
                    }
                    else
                    {
                        PinToBottom();
                        _bottomPlacementLockActive = false;
                        SetShouldAutoScroll(!_hasNewerRows());
                        if (!_hasNewerRows())
                        {
                            _onReachedLatest?.Invoke();
                        }

                        UpdateJumpToLatestVisibility();
                        var callback = _pendingBottomPlacementReleaseCompleted;
                        _pendingBottomPlacementReleaseCompleted = null;
                        callback?.Invoke();
                    }
                }
            }
        }
    }

    private void PinToBottom()
    {
        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        SetProgrammaticOffset(maxOffsetY);
    }

    private static DispatcherPriorityAwaitable YieldForRenderedContent(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.Yield(DispatcherPriority.Background);
    }

    private void UpdateJumpToLatestVisibility()
    {
        var isVisible = !_bottomPlacementLockActive && (_hasNewerRows() || !IsNearBottom());
        if (_isJumpToLatestVisible == isVisible)
        {
            return;
        }

        _isJumpToLatestVisible = isVisible;
        _setJumpToLatestVisible?.Invoke(isVisible);
    }

    private bool IsNearTop()
    {
        return _scrollViewer.Offset.Y <= ResolveLoadThreshold(_loadOlderThreshold);
    }

    private bool IsNearLoadTop() => IsNearTop();

    private bool IsNearLoadBottom()
    {
        return DistanceFromBottom() <= ResolveLoadThreshold(_loadNewerThreshold);
    }

    private bool IsNearBottom()
    {
        return DistanceFromBottom() <= _autoScrollThreshold;
    }

    private double DistanceFromBottom() =>
        _scrollViewer.Extent.Height - (_scrollViewer.Offset.Y + _scrollViewer.Viewport.Height);

    private double MaxOffsetY() => Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);

    private double ResolveLoadThreshold(double configuredThreshold)
        => Math.Max(configuredThreshold, _scrollViewer.Viewport.Height * ViewportLoadThresholdRatio);

    private sealed record ScrollAnchor(
        ScrollAnchorMode Mode,
        bool WasNearBottom,
        double DistanceFromBottom,
        double OffsetY,
        double ExtentHeight,
        long InteractionRevision,
        IReadOnlyList<ItemAnchor> Items);

    private sealed record ItemAnchor(object Item, double Top, double Bottom);

    private enum ScrollAnchorMode
    {
        LiveTranscriptMutation,
        ViewportMutation,
        OlderRowsMutation,
    }

}

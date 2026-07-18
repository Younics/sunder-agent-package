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
    private const double TrueBottomEpsilon = 1;
    private const double ViewportLoadThresholdRatio = 0.25;
    private const int AnchorRestorationMaxRenderPasses = 8;
    private const int AnchorRestorationStableRenderPasses = 2;
    private const int BottomPlacementMaxRenderPasses = 8;
    private const int BottomPlacementStableRenderPasses = 2;

    private readonly ScrollViewer _scrollViewer;
    private readonly Control? _itemsControl;
    private readonly Func<bool> _canLoadOlderRows;
    private readonly Func<object?, CancellationToken, Task<bool>> _loadOlderRowsAsync;
    private readonly Func<bool> _canLoadNewerRows;
    private readonly Func<object?, CancellationToken, Task<bool>> _loadNewerRowsAsync;
    private readonly Func<bool> _hasNewerRows;
    private readonly Func<bool> _isFollowingLatest;
    private readonly Action<bool>? _setJumpToLatestVisible;
    private readonly Func<bool>? _onDetachedFromLatest;
    private readonly Func<bool>? _onReachedLatest;
    private readonly Action<TranscriptViewportAnchorData?>? _setViewportAnchor;
    private readonly Action<Exception>? _pagingFailed;
    private readonly Func<IEnumerable<(object Item, Control Visual)>>? _enumerateRealizedAnchors;
    private readonly Func<object, Control?>? _realizeAnchorVisual;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _anchorRestorationGate = new(1, 1);
    private CancellationTokenSource _presentationPagingCancellation;
    private readonly double _autoScrollThreshold;
    private readonly double _loadOlderThreshold;
    private readonly double _loadNewerThreshold;
    private bool _userDetached;
    private bool _forceScrollToBottomOnNextTranscriptChanged;
    private bool _isJumpToLatestVisible;
    private bool _isProgrammaticScroll;
    private bool _isRestoringAnchor;
    private bool _scrollToBottomPending;
    private bool _presentationActive = true;
    private bool _bottomPlacementLockActive;
    private bool _restoreAnchorPending;
    private bool _loadOlderPending;
    private bool _loadNewerPending;
    private bool _suppressEdgeLoadsUntilNextScroll;
    private bool _isOlderEdgeArmed = true;
    private bool _isNewerEdgeArmed = true;
    private long _loadNewerResumeInteractionRevision = -1;
    private long _pagingContextRevision;
    private long _activePageInteractionRevision = -1;
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
    private Task _userScrollEvaluationOperation = Task.CompletedTask;
    private ScrollToBottomRequest? _pendingScrollToBottomRequest;
    private bool _disposed;

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        Control? itemsControl,
        Func<bool> canLoadOlderRows,
        Func<object?, CancellationToken, Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<object?, CancellationToken, Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Func<bool>? isFollowingLatest = null,
        Action<bool>? setJumpToLatestVisible = null,
        Func<bool>? onDetachedFromLatest = null,
        Func<bool>? onReachedLatest = null,
        Action<TranscriptViewportAnchorData?>? setViewportAnchor = null,
        Action<Exception>? pagingFailed = null,
        Func<IEnumerable<(object Item, Control Visual)>>? enumerateRealizedAnchors = null,
        Func<object, Control?>? realizeAnchorVisual = null,
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
        _isFollowingLatest = isFollowingLatest ?? (() => true);
        _setJumpToLatestVisible = setJumpToLatestVisible;
        _onDetachedFromLatest = onDetachedFromLatest;
        _onReachedLatest = onReachedLatest;
        _setViewportAnchor = setViewportAnchor;
        _pagingFailed = pagingFailed;
        _enumerateRealizedAnchors = enumerateRealizedAnchors;
        _realizeAnchorVisual = realizeAnchorVisual;
        _autoScrollThreshold = autoScrollThreshold;
        _loadOlderThreshold = loadOlderThreshold;
        _loadNewerThreshold = loadNewerThreshold;
        _presentationPagingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _lastObservedOffsetY = scrollViewer.Offset.Y;
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
        Func<bool>? isFollowingLatest = null,
        Action<bool>? setJumpToLatestVisible = null,
        Func<bool>? onDetachedFromLatest = null,
        Func<bool>? onReachedLatest = null,
        Action<TranscriptViewportAnchorData?>? setViewportAnchor = null,
        Action<Exception>? pagingFailed = null,
        Func<IEnumerable<(object Item, Control Visual)>>? enumerateRealizedAnchors = null,
        Func<object, Control?>? realizeAnchorVisual = null,
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
            isFollowingLatest,
            setJumpToLatestVisible,
            onDetachedFromLatest,
            onReachedLatest,
            setViewportAnchor,
            pagingFailed,
            enumerateRealizedAnchors,
            realizeAnchorVisual,
            autoScrollThreshold,
            loadOlderThreshold,
            loadNewerThreshold)
    {
    }

    public void BeginTranscriptMutation()
    {
        if (_disposed || _isRestoringAnchor)
        {
            return;
        }

        _pendingAnchor ??= CaptureScrollAnchor(ScrollAnchorMode.LiveTranscriptMutation);
    }

    public void BeginTranscriptReplacementMutation()
    {
        if (_disposed)
        {
            return;
        }

        InvalidatePendingScrollOperations();
        _pendingAnchor = CaptureScrollAnchor(ScrollAnchorMode.LiveTranscriptMutation);
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
        if (_pendingAnchor?.Mode == ScrollAnchorMode.LiveTranscriptMutation)
        {
            _pendingAnchor = null;
        }
    }

    public void SetPresentationActive(bool isActive)
    {
        if (_disposed || _presentationActive == isActive)
        {
            return;
        }

        if (!isActive && _activePageInteractionRevision != _interactionRevision)
        {
            CaptureViewportAnchor();
        }

        _presentationActive = isActive;
        if (isActive)
        {
            if (_presentationPagingCancellation.IsCancellationRequested)
            {
                _presentationPagingCancellation.Dispose();
                _presentationPagingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token);
            }
        }
        else
        {
            _presentationPagingCancellation.Cancel();
        }
        _interactionRevision++;
        _pagingContextRevision++;
        _lastObservedOffsetY = _scrollViewer.Offset.Y;
        _pendingAnchor = null;
        _pendingScrollToBottomRequest = null;
        _forceScrollToBottomOnNextTranscriptChanged = false;
        _userScrollPending = false;
        _pendingUserScrollCanResumeFollowing = false;
        _pendingUserScrollDirection = UserScrollDirection.None;
        _lastUserScrollDirection = UserScrollDirection.None;
        _scrollBarInteractionActive = false;
        _touchScrollStart = null;
        _touchNestedScrollViewer = null;
        _touchScrollRecognized = false;
        _loadNewerResumeInteractionRevision = -1;
        if (isActive)
        {
            CancelBottomPlacementLockForUserInteraction();
        }
        else
        {
            CancelBottomPlacementLock(invokeCompletion: false);
        }
        UpdateJumpToLatestVisibility();
    }

    public void BeginInitialPlacement()
    {
        if (_disposed)
        {
            return;
        }

        InvalidatePendingScrollOperations();
        PrepareToFollowTail();
        _lastUserScrollDirection = UserScrollDirection.None;
        _pendingUserScrollDirection = UserScrollDirection.None;
        _userScrollPending = false;
        _suppressEdgeLoadsUntilNextScroll = false;
        _isOlderEdgeArmed = true;
        _isNewerEdgeArmed = true;
        UpdateJumpToLatestVisibility();
    }

    public void RestoreViewportAnchor(TranscriptViewportAnchorData? viewportAnchor)
    {
        if (_disposed || !_presentationActive || viewportAnchor is not { } anchor)
        {
            return;
        }

        var items = anchor.AnchorKey is not null && anchor.AnchorViewportTop is { } anchorTop
            ? new[] { new ItemAnchor(anchor.AnchorKey, anchorTop, anchorTop) }
            : [];
        _pendingAnchor = new ScrollAnchor(
            ScrollAnchorMode.ViewportMutation,
            WasFollowingTail: false,
            anchor.DistanceFromBottom,
            anchor.OffsetY,
            _scrollViewer.Extent.Height,
            _interactionRevision,
            items);
        QueueRestoreScrollAnchor();
    }

    public void OnTranscriptChanged()
    {
        if (_disposed || !_presentationActive)
        {
            return;
        }

        UpdateJumpToLatestVisibility();
        if (_forceScrollToBottomOnNextTranscriptChanged)
        {
            _forceScrollToBottomOnNextTranscriptChanged = false;
            if (_forceScrollToBottomInteractionRevision == _interactionRevision)
            {
                _pendingAnchor = null;
                QueueScrollToBottom(force: true);
                return;
            }
        }

        if (_pendingAnchor is not null)
        {
            QueueRestoreScrollAnchor();
            return;
        }

        if (_loadOlderPending || _loadNewerPending)
        {
            return;
        }

        if (IsFollowingTail && QueueLoadNewerRowsIfAtBottom(requireActualBottom: true))
        {
            return;
        }

        if (IsFollowingTail && !_hasNewerRows())
        {
            QueueScrollToBottom();
        }
    }

    public void ForceScrollToBottomOnNextTranscriptChanged()
    {
        InvalidatePendingScrollOperations();
        _forceScrollToBottomOnNextTranscriptChanged = true;
        _forceScrollToBottomInteractionRevision = _interactionRevision;
        PrepareToFollowTail();
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
            InvalidatePendingScrollOperations();
            PrepareToFollowTail();
        }

        _pendingScrollToBottomRequest = new ScrollToBottomRequest(_interactionRevision, force);
        if (_scrollToBottomPending)
        {
            return;
        }

        _scrollToBottomPending = true;
        var operation = InvokeOnDispatcherAsync(() =>
        {
            _scrollToBottomPending = false;
            var request = _pendingScrollToBottomRequest;
            _pendingScrollToBottomRequest = null;
            if (_disposed
                || !_presentationActive
                || request is null
                || request.InteractionRevision != _interactionRevision
                || !request.Force && !IsFollowingTail)
            {
                return;
            }

            ScrollToBottom(resumeFollowing: request.Force || IsFollowingTail);
        }, DispatcherPriority.Render);
        _scrollToBottomOperation = ObservePagingOperationAsync(operation);
    }

    private static async Task InvokeOnDispatcherAsync(Action action, DispatcherPriority priority)
        => await Dispatcher.UIThread.InvokeAsync(action, priority);

    public void QueueScrollToBottomAfterLayoutSettles(
        Action? completed = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !_presentationActive || !IsFollowingTail)
        {
            completed?.Invoke();
            return;
        }

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
                QueueScrollToBottom();
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
        => _presentationActive
           && IsFollowingTail
           && _tailFollowInteractionRevision == _interactionRevision
           && !_hasNewerRows()
           && !_isRestoringAnchor
           && _pendingAnchor is null
           && !_loadOlderPending
           && !_loadNewerPending;

    private void OnScrollOffsetChanged()
    {
        var offsetY = _scrollViewer.Offset.Y;
        var offsetDelta = offsetY - _lastObservedOffsetY;
        _lastObservedOffsetY = offsetY;
        if (_isProgrammaticScroll)
        {
            return;
        }

        if (_bottomPlacementLockActive)
        {
            UpdateJumpToLatestVisibility();
            return;
        }

        if (_isRestoringAnchor)
        {
            UpdateJumpToLatestVisibility();
            return;
        }

        var userScroll = ResolveUserScrollDirection(offsetDelta);
        var userDirection = userScroll.Direction;
        if (userDirection == UserScrollDirection.None)
        {
            UpdateJumpToLatestVisibility();
            return;
        }

        _suppressEdgeLoadsUntilNextScroll = false;
        _lastUserScrollDirection = userDirection;
        if (userDirection == UserScrollDirection.TowardHistory)
        {
            DetachFromLatestForUser();
        }

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

        if (_loadOlderPending || _loadNewerPending)
        {
            if (_loadNewerPending
                && userDirection == UserScrollDirection.TowardTail
                && userScroll.CanResumeFollowing
                && IsAtBottom())
            {
                _loadNewerResumeInteractionRevision = _interactionRevision;
            }
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
            if (userDirection == UserScrollDirection.TowardTail)
            {
                QueueLoadNewerRows(userScroll.CanResumeFollowing && IsAtBottom());
                if (userScroll.CanResumeFollowing)
                {
                    TryResumeFollowingAtBottom();
                }
            }
        }

        UpdateJumpToLatestVisibility();
    }

    internal bool QueueLoadOlderRows()
    {
        if (_disposed || !_presentationActive)
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
        var pagingContextRevision = _pagingContextRevision;
        _activePageInteractionRevision = interactionRevision;
        _loadOlderPending = true;

        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadOlderRowsAsync(
                anchor,
                interactionRevision,
                pagingContextRevision,
                _presentationPagingCancellation.Token),
            DispatcherPriority.Background);
        _loadOlderOperation = ObservePagingOperationAsync(operation);

        return true;
    }

    internal bool QueueLoadNewerRows(bool resumeFollowingWhenCaughtUp = false)
    {
        if (_disposed || !_presentationActive)
        {
            return false;
        }

        if (_loadNewerPending)
        {
            if (resumeFollowingWhenCaughtUp)
            {
                _loadNewerResumeInteractionRevision = _interactionRevision;
            }
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
        var wasFollowingTail = IsFollowingTail;
        var interactionRevision = anchor.InteractionRevision;
        var pagingContextRevision = _pagingContextRevision;
        _activePageInteractionRevision = interactionRevision;
        _loadNewerResumeInteractionRevision = resumeFollowingWhenCaughtUp
            ? interactionRevision
            : -1;
        _loadNewerPending = true;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadNewerRowsAsync(
                anchor,
                wasFollowingTail,
                interactionRevision,
                pagingContextRevision,
                _presentationPagingCancellation.Token),
            DispatcherPriority.Background);
        _loadNewerOperation = ObservePagingOperationAsync(operation);

        return true;
    }

    private bool QueueLoadOlderRowsIfNearTop()
    {
        if (IsFollowingTail || !_isOlderEdgeArmed || !IsNearLoadTop())
        {
            return false;
        }

        return QueueLoadOlderRows();
    }

    private bool QueueLoadNewerRowsIfAtBottom(bool requireActualBottom)
    {
        if (!_hasNewerRows())
        {
            return false;
        }

        if (requireActualBottom ? !IsAtBottom() : !IsNearLoadBottom())
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

        return QueueLoadNewerRows(
            resumeFollowingWhenCaughtUp: IsFollowingTail && IsAtBottom());
    }

    private void ScrollToBottom(bool resumeFollowing = true)
    {
        PinToBottom();
        if (resumeFollowing && !_hasNewerRows())
        {
            ResumeFollowingLatest();
        }

        UpdateJumpToLatestVisibility();
    }

    private void TryResumeFollowingAtBottom()
    {
        if (_lastUserScrollDirection == UserScrollDirection.TowardTail
            && !_hasNewerRows()
            && IsAtBottom())
        {
            ResumeFollowingLatest();
        }
    }

    private void UpdateJumpToLatestVisibility()
    {
        var isVisible = !_bottomPlacementLockActive
                        && (_hasNewerRows() || !IsFollowingTail || !IsNearBottom());
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

    private bool IsAtBottom() => DistanceFromBottom() <= TrueBottomEpsilon;

    private double DistanceFromBottom() =>
        Math.Max(0, _scrollViewer.Extent.Height - (_scrollViewer.Offset.Y + _scrollViewer.Viewport.Height));

    private double MaxOffsetY() => Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);

    private double ResolveLoadThreshold(double configuredThreshold)
        => Math.Max(configuredThreshold, _scrollViewer.Viewport.Height * ViewportLoadThresholdRatio);

    private sealed record ScrollAnchor(
        ScrollAnchorMode Mode,
        bool WasFollowingTail,
        double DistanceFromBottom,
        double OffsetY,
        double ExtentHeight,
        long InteractionRevision,
        IReadOnlyList<ItemAnchor> Items);

    private sealed record ScrollToBottomRequest(long InteractionRevision, bool Force);

    private sealed record ItemAnchor(object Item, double Top, double Bottom);

    private enum ScrollAnchorMode
    {
        LiveTranscriptMutation,
        ViewportMutation,
        OlderRowsMutation,
    }

}

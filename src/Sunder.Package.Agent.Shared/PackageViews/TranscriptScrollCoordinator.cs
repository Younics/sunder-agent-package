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
    private const int AnchorRestorationMaxRenderPasses = 16;
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
    private readonly Func<Control?>? _realizeTailVisual;
    private readonly Func<CancellationToken, Task> _waitForViewportMutationWatchdog;
    private readonly TranscriptScrollAnchorHost? _anchorHost;
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
    private bool _isOlderEdgeArmed = true;
    private bool _isNewerEdgeArmed = true;
    private long _loadNewerResumeInteractionRevision = -1;
    private long _pagingContextRevision;
    private long _activePageInteractionRevision = -1;
    private int _bottomPlacementLockVersion;
    private long _bottomPlacementAuthorityRevision;
    private Action? _pendingSettledScrollCompleted;
    private Action? _pendingBottomPlacementReleaseCompleted;
    private ScrollAnchor? _pendingAnchor;
    private ScrollAnchor? _olderPagingAnchor;
    private ScrollAnchor? _newerPagingAnchor;
    private IDisposable? _bottomPlacementAnchoringSuspension;
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
        double loadNewerThreshold = DefaultLoadNewerThreshold,
        TranscriptScrollAnchorHost? anchorHost = null,
        Func<Control?>? realizeTailVisual = null,
        Func<CancellationToken, Task>? waitForViewportMutationWatchdog = null,
        TimeProvider? viewportMutationTimeProvider = null)
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
        _realizeTailVisual = realizeTailVisual;
        _waitForViewportMutationWatchdog = waitForViewportMutationWatchdog
            ?? (cancellationToken => WaitForDefaultViewportMutationWatchdogAsync(
                viewportMutationTimeProvider ?? TimeProvider.System,
                cancellationToken));
        _anchorHost = anchorHost;
        _autoScrollThreshold = autoScrollThreshold;
        _loadOlderThreshold = loadOlderThreshold;
        _loadNewerThreshold = loadNewerThreshold;
        _presentationPagingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _lastObservedOffsetY = scrollViewer.Offset.Y;
        _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
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
        _scrollViewer.AddHandler(
            InputElement.ScrollGestureEvent,
            OnUserScrollGesture,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _scrollViewer.AddHandler(
            InputElement.ScrollGestureEndedEvent,
            OnUserScrollGestureEnded,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _anchorHost?.SetFollowingTail(IsFollowingTail);
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
        double loadNewerThreshold = DefaultLoadNewerThreshold,
        TranscriptScrollAnchorHost? anchorHost = null,
        Func<Control?>? realizeTailVisual = null,
        Func<CancellationToken, Task>? waitForViewportMutationWatchdog = null,
        TimeProvider? viewportMutationTimeProvider = null)
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
            loadNewerThreshold,
            anchorHost,
            realizeTailVisual,
            waitForViewportMutationWatchdog,
            viewportMutationTimeProvider)
    {
    }

    public void SetPresentationActive(bool isActive)
    {
        if (_disposed || _presentationActive == isActive)
        {
            return;
        }

        SupersedeViewportMutationForAuthority(TranscriptViewportMutationStatus.PresentationInactive);
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
            _anchorHost?.ReleaseTrailingCompensator();
        }
        _interactionRevision++;
        _pagingContextRevision++;
        _lastObservedOffsetY = _scrollViewer.Offset.Y;
        _pendingScrollToBottomRequest = null;
        _captureViewportAnchorOnScrollChanged = false;
        _forceScrollToBottomOnNextTranscriptChanged = false;
        _userScrollPending = false;
        _pendingUserScrollCanResumeFollowing = false;
        _pendingUserScrollDirection = UserScrollDirection.None;
        _lastUserScrollDirection = UserScrollDirection.None;
        _scrollBarInteractionActive = false;
        _touchScrollStart = null;
        _touchNestedScrollViewer = null;
        _touchScrollRecognized = false;
        _scrollGestureActive = false;
        _loadNewerResumeInteractionRevision = -1;
        if (isActive)
        {
            CancelBottomPlacementLockForUserInteraction();
        }
        else
        {
            CancelBottomPlacementLock(invokeCompletion: false);
        }
        _anchorHost?.SetFollowingTail(IsFollowingTail);
        ClearPendingAnchor();
        ReleaseOlderPagingAnchor();
        ReleaseNewerPagingAnchor();
        UpdateJumpToLatestVisibility();
    }

    public void CancelInitialPlacement()
    {
        if (!_disposed)
        {
            InvalidatePendingScrollOperations();
        }
    }

    public void RestoreViewportAnchor(TranscriptViewportAnchorData? viewportAnchor)
    {
        if (_disposed || !_presentationActive || viewportAnchor is not { } anchor)
        {
            return;
        }
        if (_pendingScrollToBottomRequest is
            {
                Force: true,
                InteractionRevision: var requestRevision,
            }
            && requestRevision == _interactionRevision)
        {
            return;
        }

        SupersedeViewportMutationForAuthority();
        _anchorHost?.ReleaseTrailingCompensator();

        _anchorHost?.SetFollowingTail(false);
        var items = anchor.AnchorKey is not null && anchor.AnchorViewportTop is { } anchorTop
            ? new[] { new ItemAnchor(anchor.AnchorKey, anchorTop, anchorTop) }
            : [];
        SetPendingAnchor(new ScrollAnchor(
            ScrollAnchorMode.ExplicitViewportRestore,
            anchor.OffsetY,
            _scrollViewer.Extent.Height,
            _interactionRevision,
            _viewportAuthorityRevision,
            items,
            _anchorHost?.SuspendAnchoring()));
        QueueRestoreScrollAnchor();
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

        var requestIsForced = force
                              || _pendingScrollToBottomRequest is
                              {
                                  Force: true,
                                  InteractionRevision: var pendingRevision,
                                  AuthorityRevision: var pendingAuthorityRevision,
                              }
                              && pendingRevision == _interactionRevision
                              && pendingAuthorityRevision == _viewportAuthorityRevision;
        _pendingScrollToBottomRequest = new ScrollToBottomRequest(
            _interactionRevision,
            _viewportAuthorityRevision,
            requestIsForced);
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
                 || request.AuthorityRevision != _viewportAuthorityRevision
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
        var authorityRevision = _bottomPlacementAuthorityRevision;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => CompleteSettledScrollAsync(
                version,
                interactionRevision,
                authorityRevision,
                cancellationToken),
            DispatcherPriority.Loaded);
        _settledScrollOperation = ObservePagingOperationAsync(operation);
    }

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
        if (userDirection != UserScrollDirection.None)
        {
            ClearPendingAnchor();
        }
        if (userDirection == UserScrollDirection.None)
        {
            if (HasActiveTextSelection())
            {
                ClaimViewportAuthorityForManualInteraction(cancelPendingPaging: true);
                DetachFromLatestForUser();
                _anchorHost?.SetFollowingTail(false);
                RequestViewportAnchorCapture();
            }
            else if (_scrollViewer.IsKeyboardFocusWithin)
            {
                RequestViewportAnchorCapture();
            }
            UpdateJumpToLatestVisibility();
            return;
        }

        RequestViewportAnchorCapture();
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
        _anchorHost?.SetFollowingTail(false);
        SupersedeViewportMutationForAuthority();
        var anchor = CaptureScrollAnchor(ScrollAnchorMode.OlderRowsMutation);
        SetOlderPagingAnchor(anchor);
        var interactionRevision = anchor.InteractionRevision;
        var pagingContextRevision = _pagingContextRevision;
        _activePageInteractionRevision = interactionRevision;
        _loadOlderPending = true;
        var cancellationToken = _presentationPagingCancellation.Token;

        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadOlderRowsAsync(
                anchor,
                interactionRevision,
                pagingContextRevision,
                cancellationToken),
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
        SupersedeViewportMutationForAuthority();
        var anchor = CaptureScrollAnchor(ScrollAnchorMode.ExplicitViewportRestore);
        SetNewerPagingAnchor(anchor);
        var wasFollowingTail = IsFollowingTail;
        var interactionRevision = anchor.InteractionRevision;
        var pagingContextRevision = _pagingContextRevision;
        _activePageInteractionRevision = interactionRevision;
        _loadNewerResumeInteractionRevision = resumeFollowingWhenCaughtUp
            ? interactionRevision
            : -1;
        _loadNewerPending = true;
        var cancellationToken = _presentationPagingCancellation.Token;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadNewerRowsAsync(
                anchor,
                wasFollowingTail,
                interactionRevision,
                pagingContextRevision,
                cancellationToken),
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
        PinToBottom(TranscriptProgrammaticOffsetWriteSource.ScrollToBottom);
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
        var tailPlacementPending = IsFollowingTail
                                   && (_restoreAnchorPending
                                       || _isRestoringAnchor
                                       || _scrollToBottomPending
                                       || _pendingScrollToBottomRequest is not null);
        var isVisible = _anchorHost is not null && IsFollowingTail
            ? !_bottomPlacementLockActive && _hasNewerRows()
            : !_bottomPlacementLockActive
              && !tailPlacementPending
              && (_hasNewerRows() || !IsFollowingTail || !IsNearBottom());
        if (_isJumpToLatestVisible == isVisible)
        {
            return;
        }

        _isJumpToLatestVisible = isVisible;
        _setJumpToLatestVisible?.Invoke(isVisible);
    }

    private bool IsNearTop()
        => _scrollViewer.Offset.Y <= ResolveLoadThreshold(_loadOlderThreshold);

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

}

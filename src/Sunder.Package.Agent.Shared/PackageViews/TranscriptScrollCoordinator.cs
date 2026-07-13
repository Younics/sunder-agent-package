using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator : IDisposable
{
    private const double DefaultAutoScrollThreshold = 24;
    private const double DefaultLoadOlderThreshold = 96;
    private const double DefaultLoadNewerThreshold = 96;
    private const double ViewportLoadThresholdRatio = 0.25;
    private const int BottomPlacementMaxPasses = 18;
    private const int BottomPlacementStablePasses = 3;
    private const int BottomPlacementPostRevealPasses = 6;

    private readonly ScrollViewer _scrollViewer;
    private readonly ItemsControl? _itemsControl;
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
    private bool _settledScrollToBottomPending;
    private bool _bottomPlacementLockActive;
    private bool _bottomPlacementReleasePending;
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
        ItemsControl? itemsControl,
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
        _autoScrollThreshold = autoScrollThreshold;
        _loadOlderThreshold = loadOlderThreshold;
        _loadNewerThreshold = loadNewerThreshold;
        _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
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
            QueueScrollToBottom();
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
        _shouldAutoScroll = true;
        _pendingAnchor = null;
    }

    public void QueueScrollToBottom()
    {
        if (_scrollToBottomPending)
        {
            return;
        }

        _scrollToBottomPending = true;
        var operation = InvokeOnDispatcherAsync(() =>
        {
            _scrollToBottomPending = false;
            ScrollToBottom();
        }, DispatcherPriority.Render);
        _scrollToBottomOperation = ObservePagingOperationAsync(operation);
    }

    private static async Task InvokeOnDispatcherAsync(Action action, DispatcherPriority priority)
        => await Dispatcher.UIThread.InvokeAsync(action, priority);

    public void QueueScrollToBottomAfterLayoutSettles(Action? completed = null)
    {
        _pendingSettledScrollCompleted += completed;
        BeginBottomPlacementLock();
        if (_settledScrollToBottomPending)
        {
            return;
        }

        _settledScrollToBottomPending = true;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => CompleteSettledScrollAsync(_lifetimeCancellation.Token),
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
                PinToBottom(updateLayout: false);
                UpdateJumpToLatestVisibility();
                return;
            }

            if (ShouldPinToBottomForLayoutGrowth())
            {
                PinToBottom(updateLayout: false);
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

        _shouldAutoScroll = isNearBottom && !_hasNewerRows();
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
        var anchor = CaptureScrollAnchor(ScrollAnchorMode.ViewportMutation);
        var offsetYWhenQueued = _scrollViewer.Offset.Y;
        _loadOlderPending = true;

        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadOlderRowsAsync(anchor, offsetYWhenQueued, _lifetimeCancellation.Token),
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
        var offsetYWhenQueued = _scrollViewer.Offset.Y;
        _loadNewerPending = true;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => LoadNewerRowsAsync(
                anchor,
                wasAtBottom,
                offsetYWhenQueued,
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
        PinToBottom(updateLayout: true);
        _shouldAutoScroll = !_hasNewerRows();
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

    private async Task CompleteSettledScrollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScrollToBottomAfterLayoutSettlesAsync(cancellationToken);
        }
        finally
        {
            _settledScrollToBottomPending = false;
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                _pendingSettledScrollCompleted = null;
            }
            else
            {
                var callback = _pendingSettledScrollCompleted;
                _pendingSettledScrollCompleted = null;
                QueueReleaseBottomPlacementLock(callback);
            }
        }
    }

    private async Task ScrollToBottomAfterLayoutSettlesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pendingAnchor = null;
        _shouldAutoScroll = true;

        var previousExtentHeight = -1d;
        var previousViewportHeight = -1d;
        var stablePasses = 0;
        for (var pass = 0; pass < BottomPlacementMaxPasses; pass++)
        {
            await WaitForRenderedContentAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }
            PinToBottom(updateLayout: false);

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

            if (stablePasses >= BottomPlacementStablePasses)
            {
                break;
            }

            previousExtentHeight = extentHeight;
            previousViewportHeight = viewportHeight;
        }

        ScrollToBottom();
    }

    private void BeginBottomPlacementLock()
    {
        _bottomPlacementLockVersion++;
        _bottomPlacementLockActive = true;
        _pendingAnchor = null;
        _shouldAutoScroll = true;
        UpdateJumpToLatestVisibility();
    }

    private void QueueReleaseBottomPlacementLock(Action? completed = null)
    {
        _pendingBottomPlacementReleaseCompleted += completed;
        if (_bottomPlacementReleasePending)
        {
            return;
        }

        _bottomPlacementReleasePending = true;
        var version = _bottomPlacementLockVersion;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => ReleaseBottomPlacementLockAsync(version, _lifetimeCancellation.Token),
            DispatcherPriority.Background);
        _bottomPlacementReleaseOperation = ObservePagingOperationAsync(operation);
    }

    private async Task ReleaseBottomPlacementLockAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            for (var pass = 0; pass < BottomPlacementPostRevealPasses; pass++)
            {
                await WaitForRenderedContentAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }
                PinToBottom(updateLayout: false);
            }
        }
        finally
        {
            _bottomPlacementReleasePending = false;
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                _pendingBottomPlacementReleaseCompleted = null;
            }
            else if (version == _bottomPlacementLockVersion)
            {
                PinToBottom(updateLayout: true);
                _bottomPlacementLockActive = false;
                _shouldAutoScroll = !_hasNewerRows();
                if (!_hasNewerRows())
                {
                    _onReachedLatest?.Invoke();
                }

                UpdateJumpToLatestVisibility();
                var callback = _pendingBottomPlacementReleaseCompleted;
                _pendingBottomPlacementReleaseCompleted = null;
                callback?.Invoke();
            }
            else if (_bottomPlacementLockActive)
            {
                QueueReleaseBottomPlacementLock();
            }
        }
    }

    private void PinToBottom(bool updateLayout)
    {
        if (updateLayout)
        {
            _scrollViewer.UpdateLayout();
        }

        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        SetProgrammaticOffset(maxOffsetY);
    }

    private async Task WaitForRenderedContentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return;
        }

        _scrollViewer.UpdateLayout();
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
        IReadOnlyList<ItemAnchor> Items);

    private sealed record ItemAnchor(object Item, double Top, double Bottom);

    private enum ScrollAnchorMode
    {
        LiveTranscriptMutation,
        ViewportMutation,
    }

}

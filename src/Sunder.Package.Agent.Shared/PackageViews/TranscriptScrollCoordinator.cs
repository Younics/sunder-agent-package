using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptScrollCoordinator
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
    private readonly Func<object?, Task<bool>> _loadOlderRowsAsync;
    private readonly Func<bool> _canLoadNewerRows;
    private readonly Func<object?, Task<bool>> _loadNewerRowsAsync;
    private readonly Func<bool> _hasNewerRows;
    private readonly Action<bool>? _setJumpToLatestVisible;
    private readonly Action? _onDetachedFromLatest;
    private readonly Action? _onReachedLatest;
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

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        ItemsControl? itemsControl,
        Func<bool> canLoadOlderRows,
        Func<object?, Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<object?, Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Action<bool>? setJumpToLatestVisible = null,
        Action? onDetachedFromLatest = null,
        Action? onReachedLatest = null,
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
        _autoScrollThreshold = autoScrollThreshold;
        _loadOlderThreshold = loadOlderThreshold;
        _loadNewerThreshold = loadNewerThreshold;
        _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        UpdateJumpToLatestVisibility();
    }

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        Func<bool> canLoadOlderRows,
        Func<object?, Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<object?, Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Action<bool>? setJumpToLatestVisible = null,
        Action? onDetachedFromLatest = null,
        Action? onReachedLatest = null,
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
            autoScrollThreshold,
            loadOlderThreshold,
            loadNewerThreshold)
    {
    }

    public void BeginTranscriptMutation()
    {
        _pendingAnchor ??= CaptureScrollAnchor(ScrollAnchorMode.LiveTranscriptMutation);
    }

    public void BeginViewportMutation()
    {
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
        Dispatcher.UIThread.Post(() =>
        {
            _scrollToBottomPending = false;
            ScrollToBottom();
        }, DispatcherPriority.Render);
    }

    public void QueueScrollToBottomAfterLayoutSettles(Action? completed = null)
    {
        _pendingSettledScrollCompleted += completed;
        BeginBottomPlacementLock();
        if (_settledScrollToBottomPending)
        {
            return;
        }

        _settledScrollToBottomPending = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ScrollToBottomAfterLayoutSettlesAsync();
            }
            finally
            {
                _settledScrollToBottomPending = false;
                var callback = _pendingSettledScrollCompleted;
                _pendingSettledScrollCompleted = null;
                QueueReleaseBottomPlacementLock(callback);
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
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

    private bool QueueLoadOlderRows()
    {
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

        Dispatcher.UIThread.Post(async () =>
        {
            var loaded = false;
            try
            {
                _pendingAnchor = null;
                var protectedAnchorKey = CaptureCurrentScrollAnchorKey();
                loaded = await _loadOlderRowsAsync(protectedAnchorKey);
                if (!loaded)
                {
                    return;
                }

                _shouldAutoScroll = false;
                UpdateJumpToLatestVisibility();
            }
            finally
            {
                try
                {
                    if (loaded && Math.Abs(_scrollViewer.Offset.Y - offsetYWhenQueued) < 1)
                    {
                        await RestoreScrollAnchorAfterRenderedContentAsync(anchor);
                    }
                    else
                    {
                        await WaitForRenderedContentAsync();
                    }
                }
                finally
                {
                    if (loaded)
                    {
                        _suppressEdgeLoadsUntilNextScroll = true;
                    }

                    _loadOlderPending = false;
                }
            }
        }, DispatcherPriority.Background);

        return true;
    }

    private bool QueueLoadNewerRows()
    {
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
        Dispatcher.UIThread.Post(async () =>
        {
            var loaded = false;
            try
            {
                _pendingAnchor = null;
                var protectedAnchorKey = CaptureCurrentScrollAnchorKey();
                loaded = await _loadNewerRowsAsync(protectedAnchorKey);
                if (loaded)
                {
                    UpdateJumpToLatestVisibility();
                    if (!_hasNewerRows() || !IsNearLoadBottom())
                    {
                        NotifyReachedLatestIfCaughtUp();
                    }

                    return;
                }

                UpdateJumpToLatestVisibility();
                NotifyReachedLatestIfCaughtUp();
            }
            finally
            {
                try
                {
                    if (loaded && Math.Abs(_scrollViewer.Offset.Y - offsetYWhenQueued) < 1)
                    {
                        if (wasAtBottom)
                        {
                            await WaitForRenderedContentAsync();
                            ScrollToBottom();
                        }
                        else
                        {
                            await RestoreScrollAnchorAfterRenderedContentAsync(anchor);
                        }
                    }
                    else
                    {
                        await WaitForRenderedContentAsync();
                    }
                }
                finally
                {
                    if (loaded)
                    {
                        _suppressEdgeLoadsUntilNextScroll = true;
                    }

                    _loadNewerPending = false;
                }
            }
        }, DispatcherPriority.Background);

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

    private async Task ScrollToBottomAfterLayoutSettlesAsync()
    {
        _pendingAnchor = null;
        _shouldAutoScroll = true;

        var previousExtentHeight = -1d;
        var previousViewportHeight = -1d;
        var stablePasses = 0;
        for (var pass = 0; pass < BottomPlacementMaxPasses; pass++)
        {
            await WaitForRenderedContentAsync();
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
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                for (var pass = 0; pass < BottomPlacementPostRevealPasses; pass++)
                {
                    await WaitForRenderedContentAsync();
                    PinToBottom(updateLayout: false);
                }
            }
            finally
            {
                _bottomPlacementReleasePending = false;
                if (version == _bottomPlacementLockVersion)
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
        }, DispatcherPriority.Background);
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

    private void QueueRestoreScrollAnchor()
    {
        if (_restoreAnchorPending)
        {
            return;
        }

        _restoreAnchorPending = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _restoreAnchorPending = false;
            await RestorePendingScrollAnchorAfterRenderedContentAsync();
        }, DispatcherPriority.Render);
    }

    private async Task RestoreScrollAnchorAfterRenderedContentAsync(ScrollAnchor anchor)
    {
        _isRestoringAnchor = true;
        try
        {
            var previousExtentHeight = -1d;
            for (var pass = 0; pass < 4; pass++)
            {
                await WaitForRenderedContentAsync();
                RestoreScrollAnchor(anchor);

                var extentHeight = _scrollViewer.Extent.Height;
                if (pass > 0 && Math.Abs(extentHeight - previousExtentHeight) < 0.5)
                {
                    break;
                }

                previousExtentHeight = extentHeight;
            }
        }
        finally
        {
            _isRestoringAnchor = false;
        }
    }

    private async Task RestorePendingScrollAnchorAfterRenderedContentAsync()
    {
        var anchor = _pendingAnchor;
        if (anchor is null)
        {
            return;
        }

        await RestoreScrollAnchorAfterRenderedContentAsync(anchor);

        if (ReferenceEquals(_pendingAnchor, anchor))
        {
            _pendingAnchor = null;
        }
    }

    private async Task WaitForRenderedContentAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        _scrollViewer.UpdateLayout();
    }

    private void RestoreScrollAnchor(ScrollAnchor anchor)
    {
        if (anchor.Mode == ScrollAnchorMode.LiveTranscriptMutation && anchor.WasNearBottom)
        {
            ScrollToBottom();
            return;
        }

        foreach (var itemAnchor in anchor.Items)
        {
            if (TryGetItemTop(itemAnchor.Item, out var currentTop))
            {
                SetProgrammaticOffset(anchor.OffsetY + currentTop - itemAnchor.Top);
                _shouldAutoScroll = IsNearBottom() && !_hasNewerRows();
                UpdateJumpToLatestVisibility();
                return;
            }
        }

        RestoreScrollAnchorFallback(anchor);
    }

    private void RestoreScrollAnchorFallback(ScrollAnchor anchor)
    {
        switch (anchor.Mode)
        {
            case ScrollAnchorMode.LiveTranscriptMutation:
                SetProgrammaticOffset(MaxOffsetY() - anchor.DistanceFromBottom);
                break;
            default:
                SetProgrammaticOffset(anchor.OffsetY);
                break;
        }

        _shouldAutoScroll = IsNearBottom() && !_hasNewerRows();
        UpdateJumpToLatestVisibility();
    }

    private ScrollAnchor CaptureScrollAnchor(ScrollAnchorMode mode)
    {
        var distanceFromBottom = DistanceFromBottom();
        var itemAnchors = CaptureItemAnchors();
        return new ScrollAnchor(
            mode,
            mode == ScrollAnchorMode.LiveTranscriptMutation && IsNearBottom(),
            distanceFromBottom,
            _scrollViewer.Offset.Y,
            itemAnchors);
    }

    private object? CaptureCurrentScrollAnchorKey()
    {
        if (_scrollViewer.CurrentAnchor is Visual currentAnchor)
        {
            var rowPresenter = currentAnchor as TranscriptRowPresenter
                               ?? currentAnchor.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault();
            if (rowPresenter?.AnchorKey is { } currentAnchorKey)
            {
                return currentAnchorKey;
            }
        }

        return CaptureItemAnchors().FirstOrDefault()?.Item;
    }

    private IReadOnlyList<ItemAnchor> CaptureItemAnchors()
    {
        if (_itemsControl is null)
        {
            return [];
        }

        var viewportHeight = _scrollViewer.Viewport.Height;
        var anchors = new List<ItemAnchor>();
        foreach (var (item, visual) in EnumerateRowAnchorVisuals())
        {
            if (!TryGetTop(visual, out var top))
            {
                continue;
            }

            var bottom = top + visual.Bounds.Height;
            if (bottom <= 0 || top >= viewportHeight)
            {
                continue;
            }

            anchors.Add(new ItemAnchor(item, top, bottom));
        }

        return anchors
            .OrderBy(anchor => anchor.Top <= 0 && anchor.Bottom > 0 ? 0 : 1)
            .ThenBy(anchor => anchor.Top <= 0 ? Math.Abs(anchor.Top) : anchor.Top)
            .ToArray();
    }

    private bool TryGetItemTop(object item, out double top)
    {
        top = 0;
        if (_itemsControl is null)
        {
            return false;
        }

        foreach (var (candidateItem, visual) in EnumerateRowAnchorVisuals())
        {
            if (Equals(candidateItem, item) && TryGetTop(visual, out top))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<(object Item, Control Visual)> EnumerateRowAnchorVisuals()
    {
        if (_itemsControl is null)
        {
            yield break;
        }

        var visualsByItem = new Dictionary<object, Control>();
        foreach (var visual in _itemsControl.GetVisualDescendants().OfType<TranscriptRowPresenter>())
        {
            var item = visual.AnchorKey;
            if (item is null)
            {
                continue;
            }

            if (!visualsByItem.TryGetValue(item, out var current) || IsBetterItemAnchorVisual(visual, current))
            {
                visualsByItem[item] = visual;
            }
        }

        foreach (var (item, visual) in visualsByItem)
        {
            yield return (item, visual);
        }
    }

    private static bool IsBetterItemAnchorVisual(Control candidate, Control current)
    {
        var candidateArea = candidate.Bounds.Width * candidate.Bounds.Height;
        var currentArea = current.Bounds.Width * current.Bounds.Height;
        if (Math.Abs(candidateArea - currentArea) > 0.5)
        {
            return candidateArea > currentArea;
        }

        return candidate.Bounds.Height > current.Bounds.Height;
    }

    private bool TryGetTop(Visual visual, out double top)
    {
        var point = visual.TranslatePoint(new Point(0, 0), _scrollViewer);
        top = point?.Y ?? 0;
        return point is not null;
    }

    private void SetProgrammaticOffset(double offsetY)
    {
        _isProgrammaticScroll = true;
        try
        {
            _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, Math.Clamp(offsetY, 0, MaxOffsetY()));
        }
        finally
        {
            _isProgrammaticScroll = false;
        }
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

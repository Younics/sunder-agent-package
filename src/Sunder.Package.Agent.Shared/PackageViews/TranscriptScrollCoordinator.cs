using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptScrollCoordinator
{
    private const double DefaultAutoScrollThreshold = 24;
    private const double DefaultLoadOlderThreshold = 36;

    private readonly ScrollViewer _scrollViewer;
    private readonly ItemsControl? _itemsControl;
    private readonly Func<bool> _canLoadOlderRows;
    private readonly Func<Task<bool>> _loadOlderRowsAsync;
    private readonly Func<bool> _canLoadNewerRows;
    private readonly Func<Task<bool>> _loadNewerRowsAsync;
    private readonly Func<bool> _hasNewerRows;
    private readonly Action<bool>? _setJumpToLatestVisible;
    private readonly double _autoScrollThreshold;
    private readonly double _loadOlderThreshold;
    private bool _shouldAutoScroll = true;
    private bool _forceScrollToBottomOnNextTranscriptChanged;
    private bool _olderLoadArmed = true;
    private bool _newerLoadArmed = true;
    private bool _isJumpToLatestVisible;
    private bool _isProgrammaticScroll;
    private bool _scrollToBottomPending;
    private bool _settledScrollToBottomPending;
    private bool _restoreAnchorPending;
    private bool _loadOlderPending;
    private bool _loadNewerPending;
    private Action? _pendingSettledScrollCompleted;
    private ScrollAnchor? _pendingAnchor;

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        ItemsControl? itemsControl,
        Func<bool> canLoadOlderRows,
        Func<Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Action<bool>? setJumpToLatestVisible = null,
        double autoScrollThreshold = DefaultAutoScrollThreshold,
        double loadOlderThreshold = DefaultLoadOlderThreshold)
    {
        _scrollViewer = scrollViewer;
        _itemsControl = itemsControl;
        _canLoadOlderRows = canLoadOlderRows;
        _loadOlderRowsAsync = loadOlderRowsAsync;
        _canLoadNewerRows = canLoadNewerRows;
        _loadNewerRowsAsync = loadNewerRowsAsync;
        _hasNewerRows = hasNewerRows;
        _setJumpToLatestVisible = setJumpToLatestVisible;
        _autoScrollThreshold = autoScrollThreshold;
        _loadOlderThreshold = loadOlderThreshold;
        _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        UpdateJumpToLatestVisibility();
    }

    public TranscriptScrollCoordinator(
        ScrollViewer scrollViewer,
        Func<bool> canLoadOlderRows,
        Func<Task<bool>> loadOlderRowsAsync,
        Func<bool> canLoadNewerRows,
        Func<Task<bool>> loadNewerRowsAsync,
        Func<bool> hasNewerRows,
        Action<bool>? setJumpToLatestVisible = null,
        double autoScrollThreshold = DefaultAutoScrollThreshold,
        double loadOlderThreshold = DefaultLoadOlderThreshold)
        : this(
            scrollViewer,
            null,
            canLoadOlderRows,
            loadOlderRowsAsync,
            canLoadNewerRows,
            loadNewerRowsAsync,
            hasNewerRows,
            setJumpToLatestVisible,
            autoScrollThreshold,
            loadOlderThreshold)
    {
    }

    public void BeginTranscriptMutation()
    {
        _pendingAnchor ??= CaptureScrollAnchor(preserveBottom: true);
    }

    public void BeginViewportMutation()
    {
        _pendingAnchor ??= CaptureScrollAnchor(preserveBottom: false);
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
            QueueRestoreScrollAnchor();
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
        _newerLoadArmed = true;
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
                callback?.Invoke();
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
            UpdateJumpToLatestVisibility();
        }
    }

    private void OnScrollOffsetChanged()
    {
        if (_isProgrammaticScroll)
        {
            return;
        }

        var isNearBottom = IsNearBottom();
        var isNearTop = IsNearTop();
        _shouldAutoScroll = isNearBottom && !_hasNewerRows();
        if (!isNearTop && !_loadOlderPending)
        {
            _olderLoadArmed = true;
        }

        if (!isNearBottom && !_loadNewerPending)
        {
            _newerLoadArmed = true;
        }

        if (isNearTop)
        {
            QueueLoadOlderRows();
        }

        if (isNearBottom)
        {
            QueueLoadNewerRows();
        }

        UpdateJumpToLatestVisibility();
    }

    private void QueueLoadOlderRows()
    {
        if (_loadOlderPending || !_olderLoadArmed || !_canLoadOlderRows())
        {
            return;
        }

        _loadOlderPending = true;
        _olderLoadArmed = false;
        BeginTranscriptMutation();

        Dispatcher.UIThread.Post(async () =>
        {
            var loaded = false;
            try
            {
                loaded = await _loadOlderRowsAsync();
                if (!loaded)
                {
                    _olderLoadArmed = !_canLoadOlderRows() || !IsNearTop();
                    return;
                }

                await RestorePendingScrollAnchorAfterRenderedContentAsync();
                _shouldAutoScroll = false;
                _olderLoadArmed = !IsNearTop();
                UpdateJumpToLatestVisibility();
            }
            finally
            {
                _loadOlderPending = false;
                if (!loaded && !IsNearTop())
                {
                    _olderLoadArmed = true;
                }

                if (!loaded)
                {
                    DiscardPendingTranscriptMutation();
                }
            }
        }, DispatcherPriority.Background);
    }

    private void QueueLoadNewerRows()
    {
        if (_loadNewerPending || !_newerLoadArmed || !_canLoadNewerRows())
        {
            return;
        }

        _loadNewerPending = true;
        _newerLoadArmed = false;
        BeginTranscriptMutation();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var loaded = await _loadNewerRowsAsync();
                if (loaded)
                {
                    await RestorePendingScrollAnchorAfterRenderedContentAsync();
                    _newerLoadArmed = !_hasNewerRows() || !IsNearBottom();
                    UpdateJumpToLatestVisibility();
                    return;
                }

                DiscardPendingTranscriptMutation();
                _newerLoadArmed = !_hasNewerRows() || !IsNearBottom();
                UpdateJumpToLatestVisibility();
            }
            finally
            {
                _loadNewerPending = false;
            }
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        PinToBottom(updateLayout: true);
        _shouldAutoScroll = !_hasNewerRows();
        if (!_hasNewerRows())
        {
            _newerLoadArmed = true;
        }

        UpdateJumpToLatestVisibility();
    }

    private async Task ScrollToBottomAfterLayoutSettlesAsync()
    {
        _pendingAnchor = null;
        _shouldAutoScroll = true;

        var previousExtentHeight = -1d;
        for (var pass = 0; pass < 6; pass++)
        {
            await WaitForRenderedContentAsync();
            PinToBottom(updateLayout: false);

            var extentHeight = _scrollViewer.Extent.Height;
            if (pass > 0 && Math.Abs(extentHeight - previousExtentHeight) < 0.5 && IsNearBottom())
            {
                break;
            }

            previousExtentHeight = extentHeight;
        }

        ScrollToBottom();
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

    private async Task RestorePendingScrollAnchorAfterRenderedContentAsync()
    {
        await WaitForRenderedContentAsync();
        RestorePendingScrollAnchor();
    }

    private async Task WaitForRenderedContentAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        _scrollViewer.UpdateLayout();
    }

    private void RestorePendingScrollAnchor()
    {
        var anchor = _pendingAnchor;
        if (anchor is null)
        {
            return;
        }

        _pendingAnchor = null;
        if (anchor.WasNearBottom)
        {
            ScrollToBottom();
            return;
        }

        if (anchor.Item is not null && TryGetItemTop(anchor.Item, out var currentTop))
        {
            SetProgrammaticOffset(anchor.OffsetY + currentTop - anchor.ItemTop);
        }
        else
        {
            var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            SetProgrammaticOffset(maxOffsetY - anchor.DistanceFromBottom);
        }

        _shouldAutoScroll = IsNearBottom() && !_hasNewerRows();
        UpdateJumpToLatestVisibility();
    }

    private ScrollAnchor CaptureScrollAnchor(bool preserveBottom)
    {
        var distanceFromBottom = DistanceFromBottom();
        var itemAnchor = CaptureItemAnchor();
        return new ScrollAnchor(
            preserveBottom && IsNearBottom() && !_hasNewerRows(),
            distanceFromBottom,
            _scrollViewer.Offset.Y,
            itemAnchor?.Item,
            itemAnchor?.Top ?? 0);
    }

    private ItemAnchor? CaptureItemAnchor()
    {
        if (_itemsControl is null)
        {
            return null;
        }

        var viewportHeight = _scrollViewer.Viewport.Height;
        ItemAnchor? best = null;
        foreach (var (item, visual) in EnumerateRealizedItemVisuals())
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

            if (best is null || top < best.Top)
            {
                best = new ItemAnchor(item, top);
            }
        }

        return best;
    }

    private bool TryGetItemTop(object item, out double top)
    {
        top = 0;
        if (_itemsControl is null)
        {
            return false;
        }

        if (_itemsControl.ContainerFromItem(item) is { } container && TryGetTop(container, out top))
        {
            return true;
        }

        foreach (var (candidateItem, visual) in EnumerateRealizedItemVisuals())
        {
            if (ReferenceEquals(candidateItem, item) && TryGetTop(visual, out top))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<(object Item, Control Visual)> EnumerateRealizedItemVisuals()
    {
        if (_itemsControl is null)
        {
            yield break;
        }

        foreach (var container in _itemsControl.GetRealizedContainers())
        {
            var item = _itemsControl.ItemFromContainer(container);
            if (item is not null)
            {
                yield return (item, container);
            }
        }

    }

    private bool TryGetTop(Visual visual, out double top)
    {
        var point = visual.TranslatePoint(new Point(0, 0), _scrollViewer);
        top = point?.Y ?? 0;
        return point is not null;
    }

    private void SetProgrammaticOffset(double offsetY)
    {
        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        _isProgrammaticScroll = true;
        try
        {
            _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, Math.Clamp(offsetY, 0, maxOffsetY));
        }
        finally
        {
            _isProgrammaticScroll = false;
        }
    }

    private void UpdateJumpToLatestVisibility()
    {
        var isVisible = _hasNewerRows() || !IsNearBottom();
        if (_isJumpToLatestVisible == isVisible)
        {
            return;
        }

        _isJumpToLatestVisible = isVisible;
        _setJumpToLatestVisible?.Invoke(isVisible);
    }

    private bool IsNearTop()
    {
        return _scrollViewer.Offset.Y <= _loadOlderThreshold;
    }

    private bool IsNearBottom()
    {
        return DistanceFromBottom() <= _autoScrollThreshold;
    }

    private double DistanceFromBottom() =>
        _scrollViewer.Extent.Height - (_scrollViewer.Offset.Y + _scrollViewer.Viewport.Height);

    private sealed record ScrollAnchor(
        bool WasNearBottom,
        double DistanceFromBottom,
        double OffsetY,
        object? Item,
        double ItemTop);

    private sealed record ItemAnchor(object Item, double Top);
}

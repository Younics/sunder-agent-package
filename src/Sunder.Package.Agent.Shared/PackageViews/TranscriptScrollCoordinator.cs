using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptScrollCoordinator
{
    private const double DefaultAutoScrollThreshold = 24;
    private const double DefaultLoadOlderThreshold = 36;

    private readonly ScrollViewer _scrollViewer;
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
    private bool _loadOlderPending;
    private bool _loadNewerPending;

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
    {
        _scrollViewer = scrollViewer;
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

    public void OnTranscriptChanged()
    {
        UpdateJumpToLatestVisibility();
        if (_forceScrollToBottomOnNextTranscriptChanged)
        {
            _forceScrollToBottomOnNextTranscriptChanged = false;
            QueueScrollToBottom();
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
        }, DispatcherPriority.Background);
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
        var oldExtentHeight = _scrollViewer.Extent.Height;
        var oldOffset = _scrollViewer.Offset;

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

                await WaitForRenderedContentAsync();
                var addedHeight = Math.Max(0, _scrollViewer.Extent.Height - oldExtentHeight);
                _isProgrammaticScroll = true;
                _scrollViewer.Offset = new Vector(oldOffset.X, oldOffset.Y + addedHeight);
                _isProgrammaticScroll = false;
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
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var loaded = await _loadNewerRowsAsync();
                if (loaded)
                {
                    await ReopenNewerLoadGateAfterRenderedContentAsync();
                    return;
                }

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
        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        _isProgrammaticScroll = true;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, maxOffsetY);
        _isProgrammaticScroll = false;
        _shouldAutoScroll = !_hasNewerRows();
        if (!_hasNewerRows())
        {
            _newerLoadArmed = true;
        }

        UpdateJumpToLatestVisibility();
    }

    private async Task ReopenNewerLoadGateAfterRenderedContentAsync()
    {
        await WaitForRenderedContentAsync();
        _newerLoadArmed = !_hasNewerRows() || !IsNearBottom();
        UpdateJumpToLatestVisibility();
    }

    private static async Task WaitForRenderedContentAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

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
        var distanceFromBottom = _scrollViewer.Extent.Height - (_scrollViewer.Offset.Y + _scrollViewer.Viewport.Height);
        return distanceFromBottom <= _autoScrollThreshold;
    }
}

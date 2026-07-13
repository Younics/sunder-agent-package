using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private void QueueRestoreScrollAnchor()
    {
        if (_restoreAnchorPending)
        {
            return;
        }

        _restoreAnchorPending = true;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => RestorePendingAnchorAsync(_lifetimeCancellation.Token),
            DispatcherPriority.Render);
        _restoreAnchorOperation = ObservePagingOperationAsync(operation);
    }

    private async Task RestoreScrollAnchorAfterRenderedContentAsync(
        ScrollAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        _isRestoringAnchor = true;
        try
        {
            var previousExtentHeight = -1d;
            for (var pass = 0; pass < 4; pass++)
            {
                await WaitForRenderedContentAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

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

    private async Task RestorePendingScrollAnchorAfterRenderedContentAsync(
        CancellationToken cancellationToken = default)
    {
        var anchor = _pendingAnchor;
        if (anchor is null)
        {
            return;
        }

        await RestoreScrollAnchorAfterRenderedContentAsync(anchor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(_pendingAnchor, anchor))
        {
            _pendingAnchor = null;
        }
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
            if (!TryGetItemTop(itemAnchor.Item, out var currentTop))
            {
                continue;
            }

            SetProgrammaticOffset(anchor.OffsetY + currentTop - itemAnchor.Top);
            _shouldAutoScroll = IsNearBottom() && !_hasNewerRows();
            UpdateJumpToLatestVisibility();
            return;
        }

        SetProgrammaticOffset(anchor.Mode == ScrollAnchorMode.LiveTranscriptMutation
            ? MaxOffsetY() - anchor.DistanceFromBottom
            : anchor.OffsetY);
        _shouldAutoScroll = IsNearBottom() && !_hasNewerRows();
        UpdateJumpToLatestVisibility();
    }

    private ScrollAnchor CaptureScrollAnchor(ScrollAnchorMode mode)
    {
        var distanceFromBottom = DistanceFromBottom();
        var itemAnchors = CaptureItemAnchors();
        _setViewportAnchor?.Invoke(new TranscriptViewportAnchorData(
            CaptureCurrentScrollAnchorKey(),
            _scrollViewer.Offset.Y,
            distanceFromBottom));
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
            if (bottom > 0 && top < viewportHeight)
            {
                anchors.Add(new ItemAnchor(item, top, bottom));
            }
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
            if (visual.AnchorKey is not { } item)
            {
                continue;
            }

            if (!visualsByItem.TryGetValue(item, out var current) || IsBetterItemAnchorVisual(visual, current))
            {
                visualsByItem[item] = visual;
            }
        }

        foreach (var item in visualsByItem)
        {
            yield return (item.Key, item.Value);
        }
    }

    private static bool IsBetterItemAnchorVisual(Control candidate, Control current)
    {
        var candidateArea = candidate.Bounds.Width * candidate.Bounds.Height;
        var currentArea = current.Bounds.Width * current.Bounds.Height;
        return Math.Abs(candidateArea - currentArea) > 0.5
            ? candidateArea > currentArea
            : candidate.Bounds.Height > current.Bounds.Height;
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
}

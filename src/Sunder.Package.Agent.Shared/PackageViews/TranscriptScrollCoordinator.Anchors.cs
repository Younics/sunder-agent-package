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

        _renderedContentChangedDuringAnchorRestore = false;
        _restoreAnchorPending = true;
        var operation = Dispatcher.UIThread.InvokeAsync(
            () => RestorePendingAnchorAsync(_lifetimeCancellation.Token),
            DispatcherPriority.Render);
        _restoreAnchorOperation = ObservePagingOperationAsync(operation);
    }

    private async Task<bool> RestoreScrollAnchorAfterRenderedContentAsync(
        ScrollAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        if (anchor.InteractionRevision != _interactionRevision)
        {
            return false;
        }

        await _anchorRestorationGate.WaitAsync(cancellationToken);
        try
        {
            _isRestoringAnchor = true;
            var previousExtentHeight = -1d;
            var stablePasses = 0;
            var restoredAtLeastOnce = false;
            for (var pass = 0; pass < AnchorRestorationMaxRenderPasses; pass++)
            {
                await YieldForRenderedContent(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return false;
                }
                if (anchor.InteractionRevision != _interactionRevision)
                {
                    return false;
                }

                if (TryRealizeAnchorVisual(anchor))
                {
                    await YieldForRenderedContent(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || anchor.InteractionRevision != _interactionRevision)
                    {
                        return false;
                    }
                }

                if (anchor.Mode == ScrollAnchorMode.LiveTranscriptMutation
                    && anchor.WasFollowingTail
                    && (pass < 3 || HasPendingRenderedContent()))
                {
                    if (pass >= 3)
                    {
                        RevealTailForMeasurement();
                    }
                    stablePasses = 0;
                    previousExtentHeight = _scrollViewer.Extent.Height;
                    continue;
                }

                var anchorWasStable = RestoreScrollAnchor(anchor);
                restoredAtLeastOnce = true;
                var extentHeight = _scrollViewer.Extent.Height;
                if (pass > 0
                    && anchorWasStable
                    && !HasPendingRenderedContent()
                    && Math.Abs(extentHeight - previousExtentHeight) < 0.5)
                {
                    stablePasses++;
                }
                else
                {
                    stablePasses = 0;
                }

                if (stablePasses >= AnchorRestorationStableRenderPasses)
                {
                    break;
                }

                previousExtentHeight = extentHeight;
            }
            if (!restoredAtLeastOnce
                && !_disposed
                && anchor.InteractionRevision == _interactionRevision)
            {
                RestoreScrollAnchor(anchor);
                restoredAtLeastOnce = true;
            }

            return restoredAtLeastOnce && !HasPendingRenderedContent();
        }
        finally
        {
            try
            {
                ResumeNativeAnchoring();
            }
            finally
            {
                _isRestoringAnchor = false;
                _anchorRestorationGate.Release();
            }
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

        var restored = await RestoreScrollAnchorAfterRenderedContentAsync(anchor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (restored && ReferenceEquals(_pendingAnchor, anchor))
        {
            _pendingAnchor = null;
        }
    }

    private bool RestoreScrollAnchor(ScrollAnchor anchor)
    {
        if (anchor.InteractionRevision != _interactionRevision)
        {
            return true;
        }

        if (anchor.Mode == ScrollAnchorMode.LiveTranscriptMutation
            && anchor.WasFollowingTail
            && IsFollowingTail)
        {
            var previousOffset = _scrollViewer.Offset.Y;
            if (!_nativeAnchoringSuspended)
            {
                SuspendNativeAnchoring();
                if (TryGetRealizedTailOffset(out var realizedTailOffset))
                {
                    SetProgrammaticOffset(Math.Max(previousOffset, realizedTailOffset));
                    UpdateJumpToLatestVisibility();
                }
                else
                {
                    ScrollToBottom();
                }

                return false;
            }

            ScrollToBottom();
            return Math.Abs(_scrollViewer.Offset.Y - previousOffset) < 0.5;
        }

        var realizedAnchorTops = EnumerateRowAnchorVisuals()
            .Select(pair => TryGetTop(pair.Visual, out var top)
                ? (pair.Item, Top: (double?)top)
                : (pair.Item, Top: null))
            .Where(pair => pair.Top is not null)
            .GroupBy(pair => pair.Item)
            .ToDictionary(group => group.Key, group => group.First().Top!.Value);
        foreach (var itemAnchor in anchor.Items)
        {
            if (!realizedAnchorTops.TryGetValue(itemAnchor.Item, out var currentTop))
            {
                continue;
            }

            var restoredOffset = CalculateRestoredOffset(
                _scrollViewer.Offset.Y,
                currentTop,
                itemAnchor.Top);
            var anchorWasStable = Math.Abs(
                _scrollViewer.Offset.Y - Math.Clamp(restoredOffset, 0, MaxOffsetY())) < 0.5;
            SetProgrammaticOffset(restoredOffset);
            UpdateJumpToLatestVisibility();
            return anchorWasStable;
        }

        var fallbackOffset = anchor.Mode == ScrollAnchorMode.LiveTranscriptMutation
            ? MaxOffsetY() - anchor.DistanceFromBottom
            : anchor.Mode == ScrollAnchorMode.OlderRowsMutation
                ? anchor.OffsetY + Math.Max(0, _scrollViewer.Extent.Height - anchor.ExtentHeight)
                : anchor.OffsetY;
        var fallbackWasStable = Math.Abs(
            _scrollViewer.Offset.Y - Math.Clamp(fallbackOffset, 0, MaxOffsetY())) < 0.5;
        SetProgrammaticOffset(fallbackOffset);
        UpdateJumpToLatestVisibility();
        return fallbackWasStable;
    }

    private bool TryGetRealizedTailOffset(out double offset)
    {
        var tailBottom = EnumerateRowAnchorVisuals()
            .Select(pair => TryGetTop(pair.Visual, out var top)
                ? (double?)(top + pair.Visual.Bounds.Height)
                : null)
            .Where(bottom => bottom is not null)
            .Max();
        if (tailBottom is null)
        {
            offset = 0;
            return false;
        }

        offset = _scrollViewer.Offset.Y + tailBottom.Value - _scrollViewer.Viewport.Height;
        return true;
    }

    private void SuspendNativeAnchoring()
    {
        _nativeAnchoringSuspended = true;
        foreach (var visual in EnumerateRowAnchorVisuals()
                     .Select(pair => pair.Visual)
                     .Distinct())
        {
            _scrollViewer.UnregisterAnchorCandidate(visual);
            _suspendedNativeAnchorCandidates.Add(visual);
        }
    }

    private void ResumeNativeAnchoring()
    {
        if (!_nativeAnchoringSuspended)
        {
            return;
        }

        try
        {
            var realizedVisuals = EnumerateRowAnchorVisuals()
                .Select(pair => pair.Visual)
                .ToHashSet();
            foreach (var visual in _suspendedNativeAnchorCandidates)
            {
                if (realizedVisuals.Contains(visual))
                {
                    _scrollViewer.RegisterAnchorCandidate(visual);
                }
            }
        }
        finally
        {
            _suspendedNativeAnchorCandidates.Clear();
            _nativeAnchoringSuspended = false;
        }
    }

    private void RevealTailForMeasurement()
    {
        _itemsControl?.InvalidateMeasure();
        var tailTop = EnumerateRowAnchorVisuals()
            .Select(pair => TryGetTop(pair.Visual, out var top) ? (double?)top : null)
            .Where(top => top is not null)
            .Max();
        if (tailTop is null || tailTop < _scrollViewer.Viewport.Height - 1)
        {
            return;
        }

        SetProgrammaticOffset(
            _scrollViewer.Offset.Y + tailTop.Value - _scrollViewer.Viewport.Height + 1);
    }

    private ScrollAnchor CaptureScrollAnchor(ScrollAnchorMode mode)
    {
        var distanceFromBottom = DistanceFromBottom();
        var wasFollowingTail = mode == ScrollAnchorMode.LiveTranscriptMutation && IsFollowingTail;
        IReadOnlyList<ItemAnchor> itemAnchors = wasFollowingTail ? [] : CaptureItemAnchors();
        if (!wasFollowingTail)
        {
            var viewportAnchor = CaptureCurrentViewportAnchor(itemAnchors);
            if (viewportAnchor is not null)
            {
                itemAnchors =
                [
                    viewportAnchor,
                    .. itemAnchors.Where(item => !Equals(item.Item, viewportAnchor.Item)),
                ];
            }
            _setViewportAnchor?.Invoke(new TranscriptViewportAnchorData(
                viewportAnchor?.Item,
                _scrollViewer.Offset.Y,
                distanceFromBottom,
                viewportAnchor?.Top));
        }

        return new ScrollAnchor(
            mode,
            wasFollowingTail,
            distanceFromBottom,
            _scrollViewer.Offset.Y,
            _scrollViewer.Extent.Height,
            _interactionRevision,
            itemAnchors);
    }

    private void CaptureViewportAnchor()
    {
        var itemAnchors = CaptureItemAnchors();
        var viewportAnchor = CaptureCurrentViewportAnchor(itemAnchors);
        _setViewportAnchor?.Invoke(new TranscriptViewportAnchorData(
            viewportAnchor?.Item,
            _scrollViewer.Offset.Y,
            DistanceFromBottom(),
            viewportAnchor?.Top));
    }

    private object? CaptureCurrentScrollAnchorKey()
        => CaptureCurrentViewportAnchor()?.Item;

    private ItemAnchor? CaptureCurrentViewportAnchor(IReadOnlyList<ItemAnchor>? itemAnchors = null)
    {
        if (_scrollViewer.CurrentAnchor is Visual currentAnchor)
        {
            var rowPresenter = currentAnchor as TranscriptRowPresenter
                               ?? currentAnchor.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault();
            if (rowPresenter?.AnchorKey is { } currentAnchorKey)
            {
                var captured = itemAnchors?.FirstOrDefault(item => Equals(item.Item, currentAnchorKey));
                if (captured is not null)
                {
                    return captured;
                }

                if (TryGetTop(rowPresenter, out var top))
                {
                    return new ItemAnchor(
                        currentAnchorKey,
                        top,
                        top + rowPresenter.Bounds.Height);
                }
            }
        }

        return (itemAnchors ?? CaptureItemAnchors()).FirstOrDefault();
    }

    private bool TryRealizeAnchorVisual(ScrollAnchor anchor)
    {
        if (_realizeAnchorVisual is null)
        {
            return false;
        }

        var realizedItems = EnumerateRowAnchorVisuals()
            .Select(pair => pair.Item)
            .ToHashSet();
        foreach (var itemAnchor in anchor.Items)
        {
            if (!realizedItems.Contains(itemAnchor.Item)
                && _realizeAnchorVisual(itemAnchor.Item) is not null)
            {
                return true;
            }
        }

        return false;
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
            anchors.Add(new ItemAnchor(item, top, bottom));
        }

        var visibleAnchors = anchors
            .Where(anchor => anchor.Bottom > 0 && anchor.Top < viewportHeight)
            .ToArray();
        IEnumerable<ItemAnchor> candidates = visibleAnchors.Length > 0 ? visibleAnchors : anchors;
        return candidates
            .OrderBy(anchor => anchor.Top >= 0 ? 0 : 1)
            .ThenBy(anchor => anchor.Top >= 0 ? anchor.Top : Math.Abs(anchor.Top))
            .ToArray();
    }

    private IEnumerable<(object Item, Control Visual)> EnumerateRowAnchorVisuals()
    {
        if (_itemsControl is null)
        {
            yield break;
        }

        var visualsByItem = new Dictionary<object, Control>();
        if (_enumerateRealizedAnchors is not null)
        {
            foreach (var (item, visual) in _enumerateRealizedAnchors())
            {
                if (!visualsByItem.TryGetValue(item, out var current)
                    || IsBetterItemAnchorVisual(visual, current))
                {
                    visualsByItem[item] = visual;
                }
            }
        }
        else
        {
            foreach (var visual in _itemsControl.GetVisualDescendants().OfType<TranscriptRowPresenter>())
            {
                if (visual.AnchorKey is not { } item)
                {
                    continue;
                }

                if (!visualsByItem.TryGetValue(item, out var current)
                    || IsBetterItemAnchorVisual(visual, current))
                {
                    visualsByItem[item] = visual;
                }
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

    internal static double CalculateRestoredOffset(
        double currentOffset,
        double currentTop,
        double capturedTop)
        => currentOffset + currentTop - capturedTop;
}

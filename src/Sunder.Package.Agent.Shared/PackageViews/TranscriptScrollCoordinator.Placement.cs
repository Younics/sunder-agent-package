using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private CancellationTokenSource? _bottomPlacementWaitCancellation;

    public void QueuePlaceAnchorAfterLayout(
        object anchorKey,
        Action? completed = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !_presentationActive)
        {
            completed?.Invoke();
            return;
        }
        InvalidatePendingScrollOperations();
        _userDetached = true;
        _anchorHost?.SetFollowingTail(false);
        var interactionRevision = _interactionRevision;
        var authorityRevision = _viewportAuthorityRevision;
        var operation = Dispatcher.UIThread.InvokeAsync(
            async () =>
            {
                try
                {
                    Control? visual = null;
                    for (var pass = 0; pass < AnchorRestorationMaxRenderPasses; pass++)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Render);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_disposed
                            || interactionRevision != _interactionRevision
                            || authorityRevision != _viewportAuthorityRevision)
                        {
                            return;
                        }
                        _ = _realizeAnchorVisual?.Invoke(anchorKey);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_disposed
                            || interactionRevision != _interactionRevision
                            || authorityRevision != _viewportAuthorityRevision)
                        {
                            return;
                        }
                        visual = EnumerateRowAnchorVisuals()
                            .FirstOrDefault(pair => Equals(pair.Item, anchorKey)).Visual;
                        if (visual is not null)
                        {
                            break;
                        }
                    }

                    if (visual is null)
                    {
                        return;
                    }

                    try
                    {
                        await WaitForInitialAnchorGeometryAsync(
                            anchorKey, interactionRevision, authorityRevision, cancellationToken);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                             && !_lifetimeCancellation.IsCancellationRequested)
                    {
                        throw new TimeoutException("Transcript layout did not settle; showing the available content.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed
                        || interactionRevision != _interactionRevision
                        || authorityRevision != _viewportAuthorityRevision)
                    {
                        return;
                    }

                    visual = EnumerateRowAnchorVisuals()
                        .FirstOrDefault(pair => Equals(pair.Item, anchorKey)).Visual;
                    if (visual is null || !TryGetTop(visual, out var top))
                    {
                        return;
                    }

                    SetProgrammaticOffset(
                        _scrollViewer.Offset.Y + top - 28,
                        TranscriptProgrammaticOffsetWriteSource.InitialAnchorPlacement);
                    _setViewportAnchor?.Invoke(new TranscriptViewportAnchorData(
                        anchorKey,
                        _scrollViewer.Offset.Y,
                        DistanceFromBottom(),
                        28));
                    UpdateJumpToLatestVisibility();
                }
                finally
                {
                    completed?.Invoke();
                }
            },
            DispatcherPriority.Loaded);
        _settledScrollOperation = ObservePagingOperationAsync(operation);
    }

    private async Task CompleteSettledScrollAsync(
        int version,
        long interactionRevision,
        long authorityRevision,
        CancellationToken cancellationToken)
    {
        using var placementBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        placementBudget.CancelAfter(ViewportMutationWatchdogTimeout);
        _bottomPlacementWaitCancellation?.Cancel();
        _bottomPlacementWaitCancellation = placementBudget;
        try
        {
            await ScrollToBottomAfterLayoutSettlesAsync(
                interactionRevision,
                authorityRevision,
                placementBudget.Token);
        }
        catch (OperationCanceledException) when (placementBudget.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
            // The available tail remains visible on timeout; newer viewport
            // authority must not wait for a blocked Markdown parser to finish.
        }
        finally
        {
            if (ReferenceEquals(_bottomPlacementWaitCancellation, placementBudget))
            {
                _bottomPlacementWaitCancellation = null;
            }
            if (version == _bottomPlacementLockVersion)
            {
                if (cancellationToken.IsCancellationRequested || _disposed)
                {
                    _pendingSettledScrollCompleted = null;
                    _bottomPlacementLockActive = false;
                    ReleaseBottomPlacementAnchoringSuspension();
                }
                else if (interactionRevision != _interactionRevision
                         || authorityRevision != _viewportAuthorityRevision)
                {
                    CancelBottomPlacementLockForUserInteraction();
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
        long authorityRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (interactionRevision != _interactionRevision
            || authorityRevision != _viewportAuthorityRevision)
        {
            return;
        }

        ClearPendingAnchor();
        PrepareToFollowTail();
        // Extent is only an estimate until the repeater has laid out its tail.
        // Realize it before yielding a frame, not after a numeric bottom write.
        EnsureTailAnchorRealized();

        var previousExtentHeight = -1d;
        var previousViewportHeight = -1d;
        var stablePasses = 0;
        for (var pass = 0; pass < BottomPlacementMaxRenderPasses; pass++)
        {
            await YieldForRenderedContent(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed
                || interactionRevision != _interactionRevision
                || authorityRevision != _viewportAuthorityRevision)
            {
                return;
            }
            EnsureTailAnchorRealized();
            await Task.WhenAll(_scrollViewer.GetVisualDescendants()
                .OfType<StreamingMarkdownPresenter>()
                .Where(presenter => presenter.IsEffectivelyVisible)
                .Select(presenter => presenter.PendingRenderOperations))
                .WaitAsync(cancellationToken);
            await YieldForRenderedContent(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed
                || interactionRevision != _interactionRevision
                || authorityRevision != _viewportAuthorityRevision)
            {
                return;
            }
            PinToBottom(TranscriptProgrammaticOffsetWriteSource.BottomPlacementSettling);

            var extentHeight = _scrollViewer.Extent.Height;
            var viewportHeight = _scrollViewer.Viewport.Height;
            if (viewportHeight > 0
                && !HasPendingRenderedContent()
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

        cancellationToken.ThrowIfCancellationRequested();
        if (interactionRevision == _interactionRevision
            && authorityRevision == _viewportAuthorityRevision)
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
        _bottomPlacementAuthorityRevision = _viewportAuthorityRevision;
        ReleaseBottomPlacementAnchoringSuspension();
        _bottomPlacementAnchoringSuspension = _anchorHost?.SuspendAnchoring(
            _itemsControl is ItemsRepeater
                ? () => _scrollViewer.GetVisualDescendants().OfType<TranscriptRowPresenter>()
                    .FirstOrDefault(presenter => presenter.AnchorRole == TranscriptAnchorItemRole.TailSentinel)?.AnchorKey
                : null);
        ClearPendingAnchor();
        PrepareToFollowTail();
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
                && _bottomPlacementInteractionRevision == _interactionRevision
                && _bottomPlacementAuthorityRevision == _viewportAuthorityRevision)
            {
                PinToBottom(TranscriptProgrammaticOffsetWriteSource.BottomPlacementRelease);
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
                    ReleaseBottomPlacementAnchoringSuspension();
                }
                else if (_bottomPlacementInteractionRevision != _interactionRevision
                         || _bottomPlacementAuthorityRevision != _viewportAuthorityRevision)
                {
                    CancelBottomPlacementLockForUserInteraction();
                }
                else
                {
                    PinToBottom(TranscriptProgrammaticOffsetWriteSource.BottomPlacementRelease);
                    _bottomPlacementLockActive = false;
                    if (!_hasNewerRows())
                    {
                        ResumeFollowingLatest();
                    }

                    UpdateJumpToLatestVisibility();
                    var callback = _pendingBottomPlacementReleaseCompleted;
                    _pendingBottomPlacementReleaseCompleted = null;
                    ReleaseBottomPlacementAnchoringSuspension();
                    callback?.Invoke();
                }
            }
        }
    }

    private void PinToBottom(TranscriptProgrammaticOffsetWriteSource source)
    {
        if (_pendingAnchor is not null && !_isRestoringAnchor)
        {
            return;
        }

        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (Math.Abs(_scrollViewer.Offset.Y - maxOffsetY) > 0.1)
        {
            SetProgrammaticOffset(maxOffsetY, source);
        }
    }

    private static async Task YieldForRenderedContent(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private bool HasPendingRenderedContent()
        => HasPendingRenderedContent(_scrollViewer);

    private static bool HasPendingRenderedContent(Control root)
        => root.GetVisualDescendants()
            .OfType<StreamingMarkdownPresenter>()
            .Any(presenter => presenter.IsEffectivelyVisible && presenter.IsRenderPending);

    private async Task WaitForInitialAnchorGeometryAsync(
        object anchorKey,
        long interactionRevision,
        long authorityRevision,
        CancellationToken cancellationToken)
    {
        var previousExtent = double.NaN;
        var previousViewport = double.NaN;
        var previousTop = double.NaN;
        var stablePasses = 0;
        using var placementBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        placementBudget.CancelAfter(TimeSpan.FromSeconds(2));
        var placementToken = placementBudget.Token;
        for (var pass = 0; pass < AnchorRestorationMaxRenderPasses; pass++)
        {
            placementToken.ThrowIfCancellationRequested();
            if (_disposed
                || interactionRevision != _interactionRevision
                || authorityRevision != _viewportAuthorityRevision)
            {
                return;
            }

            _ = _realizeAnchorVisual?.Invoke(anchorKey);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var pendingOperations = _scrollViewer.GetVisualDescendants()
                .OfType<StreamingMarkdownPresenter>()
                .Where(static presenter => presenter.IsEffectivelyVisible)
                .Select(static presenter => presenter.PendingRenderOperations)
                .Where(static operation => !operation.IsCompleted)
                .ToArray();
            if (pendingOperations.Length > 0)
            {
                await Task.WhenAll(pendingOperations).WaitAsync(placementToken);
            }
            await Dispatcher.Yield(DispatcherPriority.Background);

            var visual = EnumerateRowAnchorVisuals()
                .FirstOrDefault(pair => Equals(pair.Item, anchorKey)).Visual;
            if (visual is null || !TryGetTop(visual, out var top))
            {
                stablePasses = 0;
                continue;
            }

            var extent = _scrollViewer.Extent.Height;
            var viewport = _scrollViewer.Viewport.Height;
            if (!HasPendingRenderedContent()
                && Math.Abs(extent - previousExtent) < 0.5
                && Math.Abs(viewport - previousViewport) < 0.5
                && Math.Abs(top - previousTop) < 0.5)
            {
                stablePasses++;
                if (stablePasses >= AnchorRestorationStableRenderPasses)
                {
                    return;
                }
            }
            else
            {
                stablePasses = 0;
            }

            previousExtent = extent;
            previousViewport = viewport;
            previousTop = top;
        }
        throw new TimeoutException("Transcript layout did not settle; showing the available content.");
    }
}

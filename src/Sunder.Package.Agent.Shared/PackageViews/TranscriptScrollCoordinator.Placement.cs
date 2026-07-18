using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
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
        PrepareToFollowTail();

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
                else if (_bottomPlacementInteractionRevision != _interactionRevision)
                {
                    CancelBottomPlacementLockForUserInteraction();
                }
                else
                {
                    PinToBottom();
                    _bottomPlacementLockActive = false;
                    if (!_hasNewerRows())
                    {
                        ResumeFollowingLatest();
                    }

                    UpdateJumpToLatestVisibility();
                    var callback = _pendingBottomPlacementReleaseCompleted;
                    _pendingBottomPlacementReleaseCompleted = null;
                    callback?.Invoke();
                }
            }
        }
    }

    private void PinToBottom()
    {
        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (Math.Abs(_scrollViewer.Offset.Y - maxOffsetY) > 0.1)
        {
            SetProgrammaticOffset(maxOffsetY);
        }
    }

    private static DispatcherPriorityAwaitable YieldForRenderedContent(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.Yield(DispatcherPriority.Background);
    }
}

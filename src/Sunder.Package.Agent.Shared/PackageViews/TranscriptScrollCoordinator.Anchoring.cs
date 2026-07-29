using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private void SetPendingAnchor(ScrollAnchor anchor)
    {
        if (ReferenceEquals(_pendingAnchor, anchor))
        {
            return;
        }

        var previous = _pendingAnchor;
        _pendingAnchor = anchor;
        previous?.Dispose();
    }

    private void ClearPendingAnchor(ScrollAnchor? expected = null)
    {
        if (expected is not null && !ReferenceEquals(_pendingAnchor, expected))
        {
            return;
        }

        var previous = _pendingAnchor;
        _pendingAnchor = null;
        previous?.Dispose();
    }

    private void SetOlderPagingAnchor(ScrollAnchor anchor)
    {
        _olderPagingAnchor?.Dispose();
        _olderPagingAnchor = anchor;
    }

    private void SetNewerPagingAnchor(ScrollAnchor anchor)
    {
        _newerPagingAnchor?.Dispose();
        _newerPagingAnchor = anchor;
    }

    private void ReleaseOlderPagingAnchor(ScrollAnchor? expected = null)
    {
        if (expected is not null && !ReferenceEquals(_olderPagingAnchor, expected))
        {
            expected.Dispose();
            return;
        }

        var anchor = _olderPagingAnchor;
        _olderPagingAnchor = null;
        anchor?.Dispose();
    }

    private void ReleaseNewerPagingAnchor(ScrollAnchor? expected = null)
    {
        if (expected is not null && !ReferenceEquals(_newerPagingAnchor, expected))
        {
            expected.Dispose();
            return;
        }

        var anchor = _newerPagingAnchor;
        _newerPagingAnchor = null;
        anchor?.Dispose();
    }

    private void ReleaseManualAnchoringSuspensions()
    {
        ClearPendingAnchor();
        ReleaseOlderPagingAnchor();
        ReleaseNewerPagingAnchor();
        ReleaseBottomPlacementAnchoringSuspension();
    }

    private void ReleaseBottomPlacementAnchoringSuspension()
        => Interlocked.Exchange(ref _bottomPlacementAnchoringSuspension, null)?.Dispose();

    private Control? EnsureTailAnchorRealized()
        => _realizeTailVisual?.Invoke();

    private IDisposable? SuspendAnchoringForManualScroll()
        => _anchorHost?.SuspendAnchoring();
}

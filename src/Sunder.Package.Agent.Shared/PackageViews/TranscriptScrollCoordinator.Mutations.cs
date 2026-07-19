namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    public void BeginTranscriptMutation()
    {
        if (_disposed || _isRestoringAnchor)
        {
            return;
        }

        if (_anchorHost is not null && IsFollowingTail)
        {
            _anchorHost.SetFollowingTail(true);
            _pendingAnchor = null;
            return;
        }

        _pendingAnchor ??= CaptureScrollAnchor(ScrollAnchorMode.LiveTranscriptMutation);
    }

    public void BeginTranscriptReplacementMutation()
    {
        if (_disposed)
        {
            return;
        }

        InvalidatePendingScrollOperations();
        if (_anchorHost is not null && IsFollowingTail)
        {
            PrepareToFollowTail();
            return;
        }

        _pendingAnchor = CaptureScrollAnchor(ScrollAnchorMode.LiveTranscriptMutation);
    }

    public void BeginViewportMutation()
    {
        if (_disposed)
        {
            return;
        }

        if (_anchorHost is not null && IsFollowingTail)
        {
            _anchorHost.SetFollowingTail(true);
            _pendingAnchor = null;
            return;
        }

        _pendingAnchor ??= CaptureScrollAnchor(ScrollAnchorMode.ViewportMutation);
    }

    public void OnViewportContentChanged()
    {
        UpdateJumpToLatestVisibility();
        if (_anchorHost is not null && IsFollowingTail && !_hasNewerRows())
        {
            PinToBottom();
            return;
        }

        if (_pendingAnchor is not null)
        {
            QueueRestoreScrollAnchor();
        }
    }

    public void DiscardPendingTranscriptMutation()
    {
        if (_pendingAnchor?.Mode == ScrollAnchorMode.LiveTranscriptMutation)
        {
            _pendingAnchor = null;
        }
    }

    public void OnTranscriptChanged()
    {
        if (_disposed || !_presentationActive)
        {
            return;
        }

        UpdateJumpToLatestVisibility();
        if (_forceScrollToBottomOnNextTranscriptChanged)
        {
            _forceScrollToBottomOnNextTranscriptChanged = false;
            if (_forceScrollToBottomInteractionRevision == _interactionRevision)
            {
                _pendingAnchor = null;
                QueueScrollToBottom(force: true);
                return;
            }
        }

        if (_pendingAnchor is not null)
        {
            QueueRestoreScrollAnchor();
            return;
        }

        if (_anchorHost is not null && IsFollowingTail && !_hasNewerRows())
        {
            PinToBottom();
            UpdateJumpToLatestVisibility();
            return;
        }

        if (_loadOlderPending || _loadNewerPending)
        {
            return;
        }

        if (IsFollowingTail && QueueLoadNewerRowsIfAtBottom(requireActualBottom: true))
        {
            return;
        }

        if (IsFollowingTail && !_hasNewerRows())
        {
            QueueScrollToBottom();
        }
    }

    public void ForceScrollToBottomOnNextTranscriptChanged()
    {
        InvalidatePendingScrollOperations();
        _forceScrollToBottomOnNextTranscriptChanged = true;
        _forceScrollToBottomInteractionRevision = _interactionRevision;
        PrepareToFollowTail();
        _pendingAnchor = null;
    }
}

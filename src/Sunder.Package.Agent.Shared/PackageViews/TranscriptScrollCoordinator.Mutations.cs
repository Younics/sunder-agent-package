using Avalonia.Controls;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    public void BeginTranscriptMutation()
    {
        if (_disposed || _isRestoringAnchor)
        {
            return;
        }
        if (_activeViewportMutation is
            {
                Kind: TranscriptViewportMutationKind.ToolExpansion
                    or TranscriptViewportMutationKind.StructuralLayout,
            } explicitMutation
            && IsCurrentViewportMutation(explicitMutation))
        {
            return;
        }

        BeginViewportMutationTransaction(TranscriptViewportMutationKind.LiveTranscript);
    }

    public void BeginTranscriptReplacementMutation()
    {
        if (_disposed)
        {
            return;
        }

        InvalidatePendingScrollOperations();
        BeginViewportMutationTransaction(TranscriptViewportMutationKind.TranscriptReplacement);
    }

    public void BeginViewportMutation(
        TranscriptViewportMutationKind kind = TranscriptViewportMutationKind.ToolExpansion,
        object? preferredAnchorKey = null,
        Control? scope = null,
        bool? isExpanding = null)
    {
        if (_disposed)
        {
            return;
        }

        BeginViewportMutationTransaction(
            kind,
            preferredAnchorKey,
            scope,
            isExpanding);
    }

    public long BeginViewportMutationPreparation(
        TranscriptViewportMutationKind kind,
        object preferredAnchorKey,
        Control scope,
        bool? isExpanding = null)
    {
        if (_disposed)
        {
            return 0;
        }

        return BeginViewportMutationTransaction(
                kind,
                preferredAnchorKey,
                scope,
                isExpanding,
                startWatchdog: false)?
            .Generation ?? 0;
    }

    public bool CommitViewportMutationPreparation(long generation, Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (_activeViewportMutation is not { } transaction
            || transaction.Generation != generation
            || !IsCurrentViewportMutation(transaction))
        {
            return false;
        }

        try
        {
            mutation();
        }
        finally
        {
            if (IsCurrentViewportMutation(transaction))
            {
                StartViewportMutationWatchdog(transaction);
                OnViewportContentChanged();
            }
        }
        return true;
    }

    public void CancelViewportMutationPreparation(long generation)
    {
        if (_activeViewportMutation is { } transaction
            && transaction.Generation == generation
            && IsCurrentViewportMutation(transaction))
        {
            TerminalizeViewportMutation(transaction, TranscriptViewportMutationStatus.Superseded);
        }
    }

    public void OnViewportContentChanged()
    {
        UpdateJumpToLatestVisibility();
        if (TryCompleteSynchronousToolCollapse())
        {
            return;
        }
        SignalViewportMutation(ViewportMutationSignalCause.ContentChanged);
    }

    public void DiscardPendingTranscriptMutation()
    {
        if (_activeViewportMutation is
            {
                Kind: TranscriptViewportMutationKind.LiveTranscript
                    or TranscriptViewportMutationKind.TranscriptReplacement,
            } active)
        {
            TerminalizeViewportMutation(active, TranscriptViewportMutationStatus.Superseded);
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
                ClearPendingAnchor();
                QueueScrollToBottom(force: true);
                return;
            }
        }

        SignalViewportMutation(ViewportMutationSignalCause.ContentChanged);
        if (_pendingAnchor is not null)
        {
            QueueRestoreScrollAnchor();
            return;
        }

        if (_loadOlderPending || _loadNewerPending)
        {
            return;
        }

        if (IsFollowingTail)
        {
            QueueLoadNewerRowsIfAtBottom(requireActualBottom: true);
        }
    }

    public void ForceScrollToBottomOnNextTranscriptChanged()
    {
        InvalidatePendingScrollOperations();
        _forceScrollToBottomOnNextTranscriptChanged = true;
        _forceScrollToBottomInteractionRevision = _interactionRevision;
        PrepareToFollowTail();
        ClearPendingAnchor();
    }
}

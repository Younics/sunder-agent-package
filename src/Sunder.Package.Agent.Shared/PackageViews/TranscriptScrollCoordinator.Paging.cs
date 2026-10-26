using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private long _pagingReevaluationGeneration;
    private long _queuedPagingReevaluationGeneration;
    private object? _activePageProtectedAnchorKey;
    private Task _pagingReevaluationOperation = Task.CompletedTask;
    private const int MaximumConsecutiveEdgePages = 8;
    private int _consecutiveEdgePages;

    private void ResetPagingContinuation()
    {
        _consecutiveEdgePages = 0;
        _isOlderEdgeArmed = true;
        _isNewerEdgeArmed = true;
    }

    internal Task PendingPagingOperations => WaitForPendingOperationsAsync();

    public void ReevaluatePagingEdges()
    {
        if (_disposed
            || !_presentationActive
            || _restoreAnchorPending
            || _isRestoringAnchor
            || _pendingAnchor is not null)
        {
            return;
        }

        // Reflow and bounded-window trimming can put the viewport at the other
        // edge without any new input. Do not reverse the reader's paging intent.
        if (_lastUserScrollDirection != UserScrollDirection.TowardTail
            && QueueLoadOlderRowsIfNearTop())
        {
            return;
        }
        if (_lastUserScrollDirection != UserScrollDirection.TowardHistory)
        {
            QueueLoadNewerRowsIfAtBottom(requireActualBottom: false);
        }
    }

    private async Task WaitForPendingOperationsAsync()
    {
        while (true)
        {
            var operations = new[]
            {
                _loadOlderOperation,
                _loadNewerOperation,
                _settledScrollOperation,
                _bottomPlacementReleaseOperation,
                _restoreAnchorOperation,
                _scrollToBottomOperation,
                _focusBringIntoViewOperation,
                _userScrollEvaluationOperation,
                _viewportMutationOperation,
                _viewportMutationCompletionOperation,
                _viewportMutationWatchdogOperation,
                _pagingReevaluationOperation,
            };
            await Task.WhenAll(operations);
            if (ReferenceEquals(operations[0], _loadOlderOperation)
                && ReferenceEquals(operations[1], _loadNewerOperation)
                && ReferenceEquals(operations[2], _settledScrollOperation)
                && ReferenceEquals(operations[3], _bottomPlacementReleaseOperation)
                && ReferenceEquals(operations[4], _restoreAnchorOperation)
                && ReferenceEquals(operations[5], _scrollToBottomOperation)
                && ReferenceEquals(operations[6], _focusBringIntoViewOperation)
                && ReferenceEquals(operations[7], _userScrollEvaluationOperation)
                && ReferenceEquals(operations[8], _viewportMutationOperation)
                && ReferenceEquals(operations[9], _viewportMutationCompletionOperation)
                && ReferenceEquals(operations[10], _viewportMutationWatchdogOperation)
                && ReferenceEquals(operations[11], _pagingReevaluationOperation))
            {
                return;
            }
        }
    }

    private void QueueReevaluatePagingEdges(long authorityRevision)
    {
        if (_disposed || !_presentationActive || authorityRevision != _viewportAuthorityRevision)
        {
            return;
        }

        var generation = ++_pagingReevaluationGeneration;
        _queuedPagingReevaluationGeneration = generation;
        var operation = InvokeOnDispatcherAsync(() =>
        {
            if (_disposed
                || !_presentationActive
                || generation != _queuedPagingReevaluationGeneration
                || authorityRevision != _viewportAuthorityRevision)
            {
                return;
            }

            _queuedPagingReevaluationGeneration = 0;
            ReevaluatePagingEdges();
        }, DispatcherPriority.Background);
        _pagingReevaluationOperation = ObservePagingOperationAsync(operation);
    }

    private async Task RestorePendingAnchorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RestorePendingScrollAnchorAfterRenderedContentAsync(cancellationToken);
        }
        finally
        {
            _restoreAnchorPending = false;
            if (_pendingAnchor is { } pendingAnchor
                && (_disposed
                    || !_presentationActive
                    || pendingAnchor.InteractionRevision != _interactionRevision
                    || pendingAnchor.AuthorityRevision != _viewportAuthorityRevision))
            {
                ClearPendingAnchor(pendingAnchor);
            }
            else if (_pendingAnchor is { } completedAnchor)
            {
                ClearPendingAnchor(completedAnchor);
            }
            else
            {
                ReevaluatePagingEdges();
            }
        }
    }

    private async Task LoadOlderRowsAsync(
        ScrollAnchor anchor,
        long interactionRevision,
        long pagingContextRevision,
        CancellationToken cancellationToken)
    {
        var loaded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interactionRevision != _interactionRevision
                || pagingContextRevision != _pagingContextRevision)
            {
                return;
            }
            ClearPendingAnchor();
            var protectedAnchorKey = anchor.Items.FirstOrDefault()?.Item
                                     ?? CaptureCurrentScrollAnchorKey();
            Volatile.Write(ref _activePageProtectedAnchorKey, protectedAnchorKey);
            loaded = await _loadOlderRowsAsync(
                new TranscriptPageAnchorAuthority(
                    protectedAnchorKey,
                    ResolveActivePageProtectedAnchorKey,
                    () =>
                    {
                        if (RefreshPagePublicationAnchor(ref anchor, pagingContextRevision, older: true))
                            interactionRevision = anchor.InteractionRevision;
                    },
                    () => CompletePagePublication(anchor, pagingContextRevision)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded
                && interactionRevision == _interactionRevision
                && pagingContextRevision == _pagingContextRevision)
            {
                _userDetached = true;
                UpdateJumpToLatestVisibility();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (pagingContextRevision == _pagingContextRevision)
            {
                ReportPagingFailure(ex);
            }
        }
        finally
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    if (loaded
                        && pagingContextRevision == _pagingContextRevision
                        && anchor.AuthorityRevision == _viewportAuthorityRevision
                        && ShouldRestoreOlderRowsAnchor(
                            interactionRevision,
                            _interactionRevision))
                    {
                        await RestoreScrollAnchorAfterRenderedContentAsync(
                            anchor,
                            cancellationToken);
                    }
                    else
                    {
                        await YieldForRenderedContent(cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (pagingContextRevision == _pagingContextRevision)
                {
                    ReportPagingFailure(ex);
                }
            }
            finally
            {
                if (ShouldRearmPagingEdge(
                        loaded,
                        interactionRevision,
                        _interactionRevision,
                        anchor.AuthorityRevision,
                        _viewportAuthorityRevision))
                {
                    _isOlderEdgeArmed = true;
                }
                _activePageInteractionRevision = -1;
                _loadOlderPending = false;
                Volatile.Write(ref _activePageProtectedAnchorKey, null);
                _anchorHost?.SetFollowingTail(IsFollowingTail);
                ReleaseOlderPagingAnchor(anchor);
                ContinuePagingAtEdge(loaded, older: true, interactionRevision, pagingContextRevision, anchor.AuthorityRevision);
            }
        }
    }

    internal static bool ShouldRestoreOlderRowsAnchor(
        long queuedUserScrollRevision,
        long currentUserScrollRevision)
        => queuedUserScrollRevision == currentUserScrollRevision;

    private async Task LoadNewerRowsAsync(
        ScrollAnchor anchor,
        bool wasFollowingTail,
        long interactionRevision,
        long pagingContextRevision,
        CancellationToken cancellationToken)
    {
        var loaded = false;
        var continueToLatest = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interactionRevision != _interactionRevision
                || pagingContextRevision != _pagingContextRevision)
            {
                return;
            }
            ClearPendingAnchor();
            var protectedAnchorKey = CaptureCurrentScrollAnchorKey();
            Volatile.Write(ref _activePageProtectedAnchorKey, protectedAnchorKey);
            loaded = await _loadNewerRowsAsync(
                new TranscriptPageAnchorAuthority(
                    protectedAnchorKey,
                    ResolveActivePageAnchor,
                    () =>
                    {
                        if (RefreshPagePublicationAnchor(ref anchor, pagingContextRevision, older: false))
                        {
                            interactionRevision = anchor.InteractionRevision;
                            wasFollowingTail = IsFollowingTail;
                        }
                    },
                    () => CompletePagePublication(anchor, pagingContextRevision)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (pagingContextRevision == _pagingContextRevision)
            {
                UpdateJumpToLatestVisibility();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (pagingContextRevision == _pagingContextRevision)
            {
                ReportPagingFailure(ex);
            }
        }
        finally
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    var tailPlaced = false;
                    var tailIntentIsCurrent = IsCurrentNewerTailIntent(
                        anchor,
                        interactionRevision,
                        pagingContextRevision);
                    if (loaded
                        && anchor.AuthorityRevision == _viewportAuthorityRevision
                        && pagingContextRevision == _pagingContextRevision)
                    {
                        if (wasFollowingTail && interactionRevision == _interactionRevision || tailIntentIsCurrent)
                        {
                            await PlaceNewerPageTailAsync(anchor, cancellationToken);
                            tailPlaced = true;
                        }
                        else if (interactionRevision == _interactionRevision)
                        {
                            await RestoreScrollAnchorAfterRenderedContentAsync(
                                anchor,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        await YieldForRenderedContent(cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_disposed
                        && loaded
                        && pagingContextRevision == _pagingContextRevision)
                    {
                        if (!tailPlaced
                            && IsCurrentNewerTailIntent(anchor, interactionRevision, pagingContextRevision))
                        {
                            await PlaceNewerPageTailAsync(anchor, cancellationToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (pagingContextRevision == _pagingContextRevision)
                {
                    ReportPagingFailure(ex);
                }
            }
            finally
            {
                var tailIntentIsCurrent = IsCurrentNewerTailIntent(
                    anchor,
                    interactionRevision,
                    pagingContextRevision);
                continueToLatest = loaded
                                   && tailIntentIsCurrent
                                   && _hasNewerRows()
                                   && _canLoadNewerRows();
                if (continueToLatest
                    || ShouldRearmPagingEdge(
                        loaded,
                        interactionRevision,
                        _interactionRevision,
                        anchor.AuthorityRevision,
                        _viewportAuthorityRevision))
                {
                    _isNewerEdgeArmed = true;
                }
                _activePageInteractionRevision = -1;
                if (!continueToLatest)
                {
                    _loadNewerResumeInteractionRevision = -1;
                }
                Volatile.Write(ref _activePageProtectedAnchorKey, null);
                _loadNewerPending = false;
                if (continueToLatest)
                {
                    if (ReserveEdgeContinuation()) QueueLoadNewerRows(resumeFollowingWhenCaughtUp: true);
                }
                ReleaseNewerPagingAnchor(anchor);
                if (!continueToLatest)
                    ContinuePagingAtEdge(loaded, older: false, interactionRevision, pagingContextRevision, anchor.AuthorityRevision);
            }
        }
    }

    private Task PlaceNewerPageTailAsync(ScrollAnchor anchor, CancellationToken cancellationToken)
    {
        // Hand the page's existing authority from reading-row preservation to
        // tail placement. Starting a new authority here would invalidate paging
        // continuation and the user's current tail intent.
        anchor.Dispose();
        var version = BeginBottomPlacementLock();
        return CompleteSettledScrollAsync(version, _interactionRevision, anchor.AuthorityRevision, cancellationToken);
    }

    private bool RefreshPagePublicationAnchor(ref ScrollAnchor anchor, long context, bool older)
    {
        if (_disposed || !_presentationActive || context != _pagingContextRevision
            || anchor.AuthorityRevision != _viewportAuthorityRevision && _manualScrollAuthorityRevision != _viewportAuthorityRevision)
            return false;

        // Input during transport supersedes the fetch-time position, but does
        // not prevent this page from preserving the reader's new position.
        // Expansion/resize/navigation authority is never reclaimed here.
        var authority = _viewportAuthorityRevision;
        _scrollViewer.UpdateLayout();
        if (_disposed || !_presentationActive || context != _pagingContextRevision || authority != _viewportAuthorityRevision)
            return false;
        var current = CaptureScrollAnchor(anchor.Mode);
        if (older) SetOlderPagingAnchor(current);
        else SetNewerPagingAnchor(current);
        anchor = current;
        _activePageInteractionRevision = current.InteractionRevision;
        Volatile.Write(ref _activePageProtectedAnchorKey, current.Items.FirstOrDefault()?.Item);
        return true;
    }

    private void CompletePagePublication(ScrollAnchor anchor, long context)
    {
        if (!_disposed && _presentationActive && context == _pagingContextRevision
            && anchor.InteractionRevision == _interactionRevision && anchor.AuthorityRevision == _viewportAuthorityRevision
            && anchor.Items.Count > 0)
            RestoreAvailableAnchor(anchor);
    }

    private TranscriptPageAnchorResolution ResolveActivePageAnchor()
        => HasCurrentNewerTailIntent()
            ? TranscriptPageAnchorResolution.Relinquish()
            : TranscriptPageAnchorResolution.Preserve(
                Volatile.Read(ref _activePageProtectedAnchorKey));

    private void ContinuePagingAtEdge(bool progressed, bool older, long interaction, long context, long authority)
    {
        if (!progressed || _disposed || !_presentationActive || _scrollViewer.Viewport.Height <= 0
            || interaction != _interactionRevision || context != _pagingContextRevision || authority != _viewportAuthorityRevision)
            return;
        if (older) _isOlderEdgeArmed = true;
        else _isNewerEdgeArmed = true;
        var needsPage = older
            ? !IsFollowingTail && IsNearLoadTop() && _canLoadOlderRows()
            : IsNearLoadBottom() && _hasNewerRows() && _canLoadNewerRows();
        if (!needsPage) { _consecutiveEdgePages = 0; return; }
        if (ReserveEdgeContinuation()) QueueReevaluatePagingEdges(authority);
    }

    private bool ReserveEdgeContinuation()
    {
        if (++_consecutiveEdgePages < MaximumConsecutiveEdgePages) return true;
        ReportPagingFailure(new InvalidDataException("Transcript paging paused after eight consecutive pages. Scroll again to continue."));
        return false;
    }

    private object? ResolveActivePageProtectedAnchorKey()
        => Volatile.Read(ref _activePageProtectedAnchorKey);

    private bool IsCurrentNewerTailIntent(
        ScrollAnchor anchor,
        long interactionRevision,
        long pagingContextRevision)
        => pagingContextRevision == _pagingContextRevision
           && anchor.AuthorityRevision == _viewportAuthorityRevision
           && _loadNewerResumeInteractionRevision == _interactionRevision
           && interactionRevision <= _interactionRevision;

    private object? CaptureCurrentPageProtectedAnchorKey()
        => CaptureCurrentScrollAnchorKey()
           ?? Volatile.Read(ref _activePageProtectedAnchorKey);

    private void RefreshActivePageProtectedAnchorKey()
    {
        if ((_loadOlderPending || _loadNewerPending)
            && CaptureCurrentPageProtectedAnchorKey() is { } anchorKey)
        {
            Volatile.Write(ref _activePageProtectedAnchorKey, anchorKey);
        }
    }

    private async Task ObservePagingOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_disposed || _lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => ReportPagingFailure(ex),
                    DispatcherPriority.Background);
            }
            catch
            {
                // Dispatcher shutdown leaves no presentation surface for the failure.
            }
        }
    }

    internal static bool ShouldRearmPagingEdge(
        bool loaded,
        long queuedInteractionRevision,
        long currentInteractionRevision,
        long queuedAuthorityRevision,
        long currentAuthorityRevision)
        => !loaded
           || queuedInteractionRevision != currentInteractionRevision
           || queuedAuthorityRevision != currentAuthorityRevision;

    private void ReportPagingFailure(Exception exception)
    {
        if (_disposed || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _pagingFailed?.Invoke(exception);
        }
        catch
        {
            // The tracked operation must not become an unobserved dispatcher failure.
        }
    }
}

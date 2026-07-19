using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
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

        QueueLoadOlderRowsIfNearTop();
        QueueLoadNewerRowsIfAtBottom(requireActualBottom: false);
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
            };
            await Task.WhenAll(operations);
            if (ReferenceEquals(operations[0], _loadOlderOperation)
                && ReferenceEquals(operations[1], _loadNewerOperation)
                && ReferenceEquals(operations[2], _settledScrollOperation)
                && ReferenceEquals(operations[3], _bottomPlacementReleaseOperation)
                && ReferenceEquals(operations[4], _restoreAnchorOperation)
                && ReferenceEquals(operations[5], _scrollToBottomOperation)
                && ReferenceEquals(operations[6], _focusBringIntoViewOperation)
                && ReferenceEquals(operations[7], _userScrollEvaluationOperation))
            {
                return;
            }
        }
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
            if (!_disposed && _presentationActive && _pendingAnchor is not null)
            {
                if (_renderedContentChangedDuringAnchorRestore)
                {
                    _renderedContentChangedDuringAnchorRestore = false;
                    QueueRestoreScrollAnchor();
                }
            }
            else
            {
                _renderedContentChangedDuringAnchorRestore = false;
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
            _pendingAnchor = null;
            var protectedAnchorKey = anchor.Items.FirstOrDefault()?.Item
                                     ?? CaptureCurrentScrollAnchorKey();
            loaded = await _loadOlderRowsAsync(protectedAnchorKey, cancellationToken);
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
                    if (!_disposed
                        && loaded
                        && interactionRevision == _interactionRevision
                        && pagingContextRevision == _pagingContextRevision)
                    {
                        _suppressEdgeLoadsUntilNextScroll = true;
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
                if (ShouldRearmPagingEdge(loaded, interactionRevision, _interactionRevision))
                {
                    _isOlderEdgeArmed = true;
                }
                _activePageInteractionRevision = -1;
                _loadOlderPending = false;
                _anchorHost?.SetFollowingTail(IsFollowingTail);
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
            _pendingAnchor = null;
            var protectedAnchorKey = CaptureCurrentScrollAnchorKey();
            loaded = await _loadNewerRowsAsync(protectedAnchorKey, cancellationToken);
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
                    if (loaded
                        && interactionRevision == _interactionRevision
                        && pagingContextRevision == _pagingContextRevision)
                    {
                        if (wasFollowingTail)
                        {
                            await YieldForRenderedContent(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!_disposed && interactionRevision == _interactionRevision)
                            {
                                ScrollToBottom(resumeFollowing: true);
                            }
                        }
                        else
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
                        if (_loadNewerResumeInteractionRevision == _interactionRevision
                            && !_hasNewerRows())
                        {
                            ScrollToBottom(resumeFollowing: true);
                        }
                        if (interactionRevision == _interactionRevision
                            && !_hasNewerRows())
                        {
                            _suppressEdgeLoadsUntilNextScroll = true;
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
                var tailIntentIsCurrent = pagingContextRevision == _pagingContextRevision
                                          && _loadNewerResumeInteractionRevision == _interactionRevision;
                continueToLatest = loaded
                                   && tailIntentIsCurrent
                                   && _hasNewerRows()
                                   && _canLoadNewerRows();
                if (continueToLatest
                    || ShouldRearmPagingEdge(loaded, interactionRevision, _interactionRevision))
                {
                    _isNewerEdgeArmed = true;
                }
                _activePageInteractionRevision = -1;
                if (!continueToLatest)
                {
                    _loadNewerResumeInteractionRevision = -1;
                }
                _loadNewerPending = false;
                if (continueToLatest)
                {
                    QueueLoadNewerRows(resumeFollowingWhenCaughtUp: true);
                }
            }
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
        long currentInteractionRevision)
        => !loaded || queuedInteractionRevision != currentInteractionRevision;

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

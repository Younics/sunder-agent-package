using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    private long _pagingReevaluationGeneration;
    private long _queuedPagingReevaluationGeneration;
    private object? _activePageProtectedAnchorKey;
    private Task _pagingReevaluationOperation = Task.CompletedTask;

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
                    ResolveActivePageProtectedAnchorKey),
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
                if (ShouldRearmPagingEdge(loaded, interactionRevision, _interactionRevision))
                {
                    _isOlderEdgeArmed = true;
                }
                _activePageInteractionRevision = -1;
                _loadOlderPending = false;
                Volatile.Write(ref _activePageProtectedAnchorKey, null);
                _anchorHost?.SetFollowingTail(IsFollowingTail);
                ReleaseOlderPagingAnchor(anchor);
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
                    ResolveActivePageProtectedAnchorKey),
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
                    if (loaded
                        && interactionRevision == _interactionRevision
                        && anchor.AuthorityRevision == _viewportAuthorityRevision
                        && pagingContextRevision == _pagingContextRevision)
                    {
                        if (wasFollowingTail)
                        {
                            await YieldForRenderedContent(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!_disposed
                                && interactionRevision == _interactionRevision
                                && anchor.AuthorityRevision == _viewportAuthorityRevision)
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
                        if (anchor.AuthorityRevision == _viewportAuthorityRevision
                            && _loadNewerResumeInteractionRevision == _interactionRevision
                            && !_hasNewerRows())
                        {
                            ScrollToBottom(resumeFollowing: true);
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
                                          && anchor.AuthorityRevision == _viewportAuthorityRevision
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
                Volatile.Write(ref _activePageProtectedAnchorKey, null);
                _loadNewerPending = false;
                if (continueToLatest)
                {
                    QueueLoadNewerRows(resumeFollowingWhenCaughtUp: true);
                }
                ReleaseNewerPagingAnchor(anchor);
            }
        }
    }

    private object? ResolveActivePageProtectedAnchorKey()
        => Volatile.Read(ref _activePageProtectedAnchorKey);

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

using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptScrollCoordinator
{
    internal Task PendingPagingOperations
        => Task.WhenAll(
            _loadOlderOperation,
            _loadNewerOperation,
            _settledScrollOperation,
            _bottomPlacementReleaseOperation,
            _restoreAnchorOperation,
            _scrollToBottomOperation,
            _focusBringIntoViewOperation);

    private async Task RestorePendingAnchorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RestorePendingScrollAnchorAfterRenderedContentAsync(cancellationToken);
        }
        finally
        {
            _restoreAnchorPending = false;
        }
    }

    private async Task LoadOlderRowsAsync(
        ScrollAnchor anchor,
        long interactionRevision,
        CancellationToken cancellationToken)
    {
        var loaded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pendingAnchor = null;
            var protectedAnchorKey = CaptureCurrentScrollAnchorKey();
            loaded = await _loadOlderRowsAsync(protectedAnchorKey, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded)
            {
                SetShouldAutoScroll(false);
                UpdateJumpToLatestVisibility();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportPagingFailure(ex);
        }
        finally
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    if (loaded && ShouldRestoreOlderRowsAnchor(
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
                    if (!_disposed && loaded)
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
                ReportPagingFailure(ex);
            }
            finally
            {
                _loadOlderPending = false;
            }
        }
    }

    internal static bool ShouldRestoreOlderRowsAnchor(
        long queuedUserScrollRevision,
        long currentUserScrollRevision)
        => queuedUserScrollRevision == currentUserScrollRevision;

    private async Task LoadNewerRowsAsync(
        ScrollAnchor anchor,
        bool wasAtBottom,
        long interactionRevision,
        CancellationToken cancellationToken)
    {
        var loaded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pendingAnchor = null;
            var protectedAnchorKey = CaptureCurrentScrollAnchorKey();
            loaded = await _loadNewerRowsAsync(protectedAnchorKey, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            UpdateJumpToLatestVisibility();
            if (!loaded || !_hasNewerRows() || !IsNearLoadBottom())
            {
                NotifyReachedLatestIfCaughtUp();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportPagingFailure(ex);
        }
        finally
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    if (loaded && interactionRevision == _interactionRevision)
                    {
                        if (wasAtBottom)
                        {
                            await YieldForRenderedContent(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!_disposed && interactionRevision == _interactionRevision)
                            {
                                ScrollToBottom();
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
                    if (!_disposed && loaded)
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
                ReportPagingFailure(ex);
            }
            finally
            {
                _loadNewerPending = false;
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

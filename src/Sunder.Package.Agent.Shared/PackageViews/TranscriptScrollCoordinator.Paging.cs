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
            _restoreAnchorOperation);

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
        double offsetYWhenQueued,
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
                _shouldAutoScroll = false;
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
                    if (loaded && Math.Abs(_scrollViewer.Offset.Y - offsetYWhenQueued) < 1)
                    {
                        await RestoreScrollAnchorAfterRenderedContentAsync(anchor, cancellationToken);
                    }
                    else
                    {
                        await WaitForRenderedContentAsync(cancellationToken);
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

    private async Task LoadNewerRowsAsync(
        ScrollAnchor anchor,
        bool wasAtBottom,
        double offsetYWhenQueued,
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
                    if (loaded && Math.Abs(_scrollViewer.Offset.Y - offsetYWhenQueued) < 1)
                    {
                        if (wasAtBottom)
                        {
                            await WaitForRenderedContentAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!_disposed)
                            {
                                ScrollToBottom();
                            }
                        }
                        else
                        {
                            await RestoreScrollAnchorAfterRenderedContentAsync(anchor, cancellationToken);
                        }
                    }
                    else
                    {
                        await WaitForRenderedContentAsync(cancellationToken);
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
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
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

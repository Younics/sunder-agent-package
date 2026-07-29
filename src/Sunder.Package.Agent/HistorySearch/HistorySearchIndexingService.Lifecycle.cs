namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            await DrainCurrentWorkerAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_lifetime.IsCancellationRequested)
            {
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
                _signal = CreateSignalChannel();
            }

            _started = true;
            var signal = _signal;
            var lifetime = _lifetime;
            try
            {
                await PrepareStartupAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _worker = RunAsync(signal, lifetime.Token);
                signal.Writer.TryWrite(true);
            }
            catch
            {
                _started = false;
                await lifetime.CancelAsync().ConfigureAwait(false);
                signal.Writer.TryComplete();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_started && _worker is null)
            {
                return;
            }
            _started = false;
            await DrainCurrentWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _sessions.TurnMutated -= OnTurnMutated;
            _sessions.TranscriptReset -= OnTranscriptReset;
            _sessions.SessionChanged -= OnSessionChanged;
            _workspaces.WorkspacesChanged -= OnWorkspacesChanged;
            _started = false;
            await DrainCurrentWorkerAsync(CancellationToken.None).ConfigureAwait(false);
            _operationGate.Dispose();
            _semanticChangeGate.Dispose();
            _lifetime.Dispose();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PrepareStartupAsync()
    {
        if (!_projection.IsAvailable)
        {
            return;
        }

        var staleGenerationInvalidated = await EnforceLocalOnlyAtStartupAsync().ConfigureAwait(false);
        var activeText = _projection.GetActiveTextGeneration();
        if (staleGenerationInvalidated)
        {
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Rebuilding,
                ProgressCompleted = 0,
                ProgressTotal = null,
                FailureCode = null,
                FailureMessage = null,
            });
            activeText = null;
        }
        _manuallyCleared = false;
        _rebuildRequested = activeText is null
                            || activeText.ExtractorVersion != HistorySearchVersions.Extractor
                            || activeText.RedactionVersion != HistorySearchVersions.Redaction
                            || _projection.WasRecovered;
        _startupReconciliationRequired = !_rebuildRequested && activeText is not null;
        _embeddingRebuildRequested = false;
        _providerRefreshRequested = false;
        if (_rebuildRequested)
        {
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Rebuilding,
                ProgressCompleted = 0,
                ProgressTotal = null,
                FailureCode = null,
                FailureMessage = null,
            });
        }
        else
        {
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Rebuilding,
                LexicalEnabled = true,
                ProgressCompleted = 0,
                ProgressTotal = null,
                FailureCode = null,
                FailureMessage = null,
            });
        }
    }

    private async Task DrainCurrentWorkerAsync(CancellationToken cancellationToken)
    {
        var worker = _worker;
        if (worker is null)
        {
            return;
        }

        var lifetime = _lifetime;
        var signal = _signal;
        await lifetime.CancelAsync().ConfigureAwait(false);
        signal.Writer.TryComplete();
        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
        }
        _worker = null;
    }
}

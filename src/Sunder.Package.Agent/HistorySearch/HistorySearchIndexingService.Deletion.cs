namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    public void DeleteSessionData(Guid sessionId)
        => DeleteSessionDataAsync(sessionId).GetAwaiter().GetResult();

    private async Task DeleteSessionDataAsync(Guid sessionId)
    {
        await _semanticChangeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var projectionChanged = false;
                try
                {
                    projectionChanged = _projection.DeleteSessionDocuments(sessionId);
                    if (projectionChanged && _projection.GetSnapshot().ActiveTextGenerationId is null)
                    {
                        lock (_pendingLock)
                        {
                            _rebuildRequested = true;
                            _startupReconciliationRequired = false;
                            _manuallyCleared = false;
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (!_projection.TryRecover(exception))
                    {
                        throw;
                    }
                    lock (_pendingLock)
                    {
                        _rebuildRequested = true;
                        _embeddingRebuildRequested = false;
                        _startupReconciliationRequired = false;
                        _manuallyCleared = false;
                    }
                    projectionChanged = true;
                }
                var configuration = _projection.GetConfiguration();
                _semanticFence.Activate(configuration.Revision);
                if (projectionChanged)
                {
                    _state.RefreshProjection();
                }
            }
            catch
            {
                _semanticFence.Activate(_projection.GetConfiguration().Revision);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _semanticChangeGate.Release();
        }
        Signal();
    }
}

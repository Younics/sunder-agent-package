namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    private async Task<bool> EnforceLocalOnlyAtStartupAsync()
    {
        var suspended = false;
        try
        {
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            suspended = true;
            var maintenance = _projection.EnforceLocalOnlyAutomaticMaintenance();
            _semanticFence.Activate(maintenance.Configuration.Revision);
            suspended = false;
            return maintenance.StaleTextGenerationInvalidated;
        }
        finally
        {
            if (suspended)
            {
                _semanticFence.Activate(_projection.GetConfiguration().Revision);
            }
        }
    }

    private async Task RefreshProviderFingerprintAtStartupAsync(CancellationToken cancellationToken)
    {
        var configuration = _projection.GetConfiguration();
        if (!configuration.SemanticEnabled
            || configuration.EmbeddingProviderPackageId is null
            || configuration.EmbeddingProviderId is null
            || configuration.EmbeddingModelId is null)
        {
            return;
        }
        var fenceSuspended = false;
        try
        {
            var selection = await _embeddingProviders.ResolveSelectionAsync(
                configuration.EmbeddingProviderPackageId,
                configuration.EmbeddingProviderId,
                configuration.EmbeddingModelId,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                    selection.SpaceFingerprint,
                    configuration.EmbeddingSpaceFingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            fenceSuspended = true;
            var next = _projection.RefreshConfigurationFingerprint(
                configuration.Revision,
                selection.SpaceFingerprint);
            _semanticFence.Activate(next.Revision);
            fenceSuspended = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // An unavailable replacement is fenced by the persisted fingerprint and stays lexical-only.
        }
        finally
        {
            if (fenceSuspended)
            {
                _semanticFence.Activate(_projection.GetConfiguration().Revision);
            }
        }
    }

    private async Task RefreshProviderFingerprintAsync(CancellationToken cancellationToken)
    {
        var configuration = _projection.GetConfiguration();
        if (!configuration.SemanticEnabled
            || configuration.EmbeddingProviderPackageId is null
            || configuration.EmbeddingProviderId is null
            || configuration.EmbeddingModelId is null)
        {
            return;
        }

        ResolvedEmbeddingSelection selection;
        try
        {
            selection = await _embeddingProviders.ResolveSelectionAsync(
                configuration.EmbeddingProviderPackageId,
                configuration.EmbeddingProviderId,
                configuration.EmbeddingModelId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ReportSemanticUnavailable(
                "embedding-provider-unavailable",
                "The selected embedding provider or model is unavailable. Lexical search remains available.");
            return;
        }

        if (string.Equals(
                selection.SpaceFingerprint,
                configuration.EmbeddingSpaceFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        await _semanticChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var fenceSuspended = false;
        var operationAcquired = false;
        try
        {
            await _semanticFence.SuspendAndDrainAsync().ConfigureAwait(false);
            fenceSuspended = true;
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            operationAcquired = true;
            var current = _projection.GetConfiguration();
            if (current.Revision != configuration.Revision
                || !current.SemanticEnabled
                || !string.Equals(current.EmbeddingProviderPackageId, selection.OwnedProvider.PackageId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.EmbeddingProviderId, selection.OwnedProvider.ProviderId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.EmbeddingModelId, selection.ModelId, StringComparison.Ordinal))
            {
                _semanticFence.Activate(current.Revision);
                fenceSuspended = false;
                return;
            }

            var next = _projection.RefreshConfigurationFingerprint(
                current.Revision,
                selection.SpaceFingerprint);
            lock (_pendingLock)
            {
                _embeddingRebuildRequested = !_manuallyCleared;
            }
            _semanticFence.Activate(next.Revision);
            fenceSuspended = false;
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Ready,
                FailureCode = null,
                FailureMessage = null,
                ProgressCompleted = 0,
                ProgressTotal = null,
            });
            Signal();
        }
        finally
        {
            if (operationAcquired)
            {
                _operationGate.Release();
            }
            if (fenceSuspended)
            {
                _semanticFence.Activate(_projection.GetConfiguration().Revision);
            }
            _semanticChangeGate.Release();
        }
    }
}

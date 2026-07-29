using System.Buffers.Binary;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    private async Task RebuildEmbeddingProjectionAsync(CancellationToken cancellationToken)
    {
        var configuration = _projection.GetConfiguration();
        if (!configuration.SemanticEnabled)
        {
            _projection.DeactivateEmbeddings();
            return;
        }
        if (configuration.EmbeddingProviderPackageId is null
            || configuration.EmbeddingProviderId is null
            || configuration.EmbeddingModelId is null
            || configuration.EmbeddingSpaceFingerprint is null
            || _projection.GetSnapshot().ActiveTextGenerationId is not { } textGenerationId)
        {
            PublishSemanticUnavailable(
                configuration,
                "embedding-configuration-incomplete",
                "Choose one embedding provider and model to enable semantic history search.");
            return;
        }

        using var lease = _semanticFence.Begin(configuration.Revision, cancellationToken);
        ResolvedEmbeddingSelection resolved;
        try
        {
            lease.ThrowIfCurrent();
            resolved = await _embeddingProviders.ResolveSelectionAsync(
                configuration.EmbeddingProviderPackageId,
                configuration.EmbeddingProviderId,
                configuration.EmbeddingModelId,
                lease.CancellationToken).ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (!string.Equals(resolved.SpaceFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
            {
                PublishSemanticUnavailable(
                    configuration,
                    "embedding-implementation-changed",
                    "The selected embedding implementation changed and must be reconfigured before vectors can be reused.");
                return;
            }
            var readiness = await _embeddingProviders
                .GetReadinessAsync(resolved, lease.CancellationToken).ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (readiness.Status != AgentProviderReadinessStatus.Ready)
            {
                PublishSemanticUnavailable(
                    configuration,
                    "embedding-provider-not-ready",
                    "The selected embedding provider is not ready. Lexical search remains available.");
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            PublishSemanticUnavailable(
                configuration,
                "embedding-provider-unavailable",
                "The selected embedding provider or model is unavailable.");
            return;
        }

        lease.ThrowIfCurrent();
        var generationId = _projection.BeginEmbeddingGeneration(textGenerationId, configuration);
        var completed = 0;
        int? dimensions = resolved.Model.Dimensions;
        try
        {
            string? continuation = null;
            while (true)
            {
                lease.ThrowIfCurrent();
                var page = _projection.ListEmbeddingWorkPage(textGenerationId, continuation, 64);
                if (page.Count == 0)
                {
                    break;
                }
                foreach (var batch in page.Chunk(EmbeddingBatchSize))
                {
                    lease.ThrowIfCurrent();
                    var results = await _embeddingProviders.GenerateEmbeddingsAsync(
                        resolved,
                        batch.Select(static item => item.BodyText).ToArray(),
                        lease.CancellationToken).ConfigureAwait(false);
                    lease.ThrowIfCurrent();
                    var currentFingerprint = await _embeddingProviders
                        .GetSpaceFingerprintAsync(resolved, lease.CancellationToken)
                        .ConfigureAwait(false);
                    lease.ThrowIfCurrent();
                    if (!string.Equals(
                            currentFingerprint,
                            configuration.EmbeddingSpaceFingerprint,
                            StringComparison.Ordinal))
                    {
                        RequestProviderRefresh();
                        throw new InvalidOperationException("The embedding provider configuration changed during indexing.");
                    }
                    if (results.Count != batch.Length)
                    {
                        throw new InvalidOperationException("Embedding provider returned an incomplete batch.");
                    }
                    for (var index = 0; index < batch.Length; index++)
                    {
                        lease.ThrowIfCurrent();
                        if (!HistoryVectorCodec.TryNormalize(
                                results[index],
                                configuration.EmbeddingModelId,
                                dimensions,
                                out var resultDimensions,
                                out var vector))
                        {
                            throw new InvalidOperationException("Embedding provider returned an invalid vector.");
                        }
                        dimensions ??= resultDimensions;
                        lease.ThrowIfCurrent();
                        _projection.SaveEmbedding(
                            generationId,
                            textGenerationId,
                            configuration,
                            batch[index],
                            resultDimensions,
                            vector);
                        completed++;
                    }
                    _state.Publish(status => status with
                    {
                        Availability = HistorySearchAvailability.Rebuilding,
                        ProgressCompleted = completed,
                    });
                }
                continuation = page[^1].DocumentId;
            }
            if (dimensions is null)
            {
                _projection.FailGeneration(generationId, "no-finalized-documents");
                PublishSemanticUnavailable(
                    configuration,
                    "no-finalized-documents",
                    "Semantic history search will become ready after finalized history is indexed.");
                return;
            }
            lease.ThrowIfCurrent();
            _projection.ActivateEmbeddingGeneration(generationId, dimensions.Value, configuration);
            lease.ThrowIfCurrent();
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Ready,
                ProgressCompleted = completed,
                ProgressTotal = completed,
                FailureCode = null,
                FailureMessage = null,
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _projection.FailGeneration(generationId, "superseded");
        }
        catch (OperationCanceledException)
        {
            _projection.FailGeneration(generationId, "cancelled");
            throw;
        }
        catch
        {
            _projection.FailGeneration(generationId, "embedding-rebuild-failed");
            PublishSemanticUnavailable(
                configuration,
                "embedding-rebuild-failed",
                "Semantic indexing failed. Lexical history search remains available.");
        }
    }

    private async Task BackfillActiveEmbeddingsAsync(CancellationToken cancellationToken)
    {
        var configuration = _projection.GetConfiguration();
        var snapshot = _projection.GetSnapshot();
        var generation = _projection.GetActiveEmbeddingGeneration();
        if (!configuration.SemanticEnabled
            || generation?.Dimensions is not { } dimensions
            || snapshot.ActiveTextGenerationId is not { } textGenerationId
            || snapshot.ActiveEmbeddingGenerationId is not { } embeddingGenerationId
            || !GenerationMatches(generation, configuration))
        {
            return;
        }

        using var lease = _semanticFence.Begin(configuration.Revision, cancellationToken);
        ResolvedEmbeddingSelection resolved;
        try
        {
            lease.ThrowIfCurrent();
            resolved = await _embeddingProviders.ResolveSelectionAsync(
                configuration.EmbeddingProviderPackageId,
                configuration.EmbeddingProviderId,
                configuration.EmbeddingModelId,
                lease.CancellationToken).ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (!string.Equals(resolved.SpaceFingerprint, configuration.EmbeddingSpaceFingerprint, StringComparison.Ordinal))
            {
                PublishSemanticUnavailable(
                    configuration,
                    "embedding-implementation-changed",
                    "The selected embedding implementation changed. Existing vectors remain fenced off.");
                return;
            }
            var readiness = await _embeddingProviders.GetReadinessAsync(resolved, lease.CancellationToken)
                .ConfigureAwait(false);
            lease.ThrowIfCurrent();
            if (readiness.Status != AgentProviderReadinessStatus.Ready)
            {
                PublishSemanticUnavailable(
                    configuration,
                    "embedding-provider-not-ready",
                    "The selected embedding provider is not ready. Lexical search remains available.");
                return;
            }

            var page = _projection.ListMissingEmbeddingWorkPage(
                textGenerationId,
                embeddingGenerationId,
                afterDocumentId: null,
                limit: EmbeddingBatchSize);
            if (page.Count == 0)
            {
                _projection.MarkSemanticReadyIfComplete(
                    textGenerationId,
                    embeddingGenerationId,
                    dimensions,
                    configuration);
                _state.Publish(status => status with
                {
                    Availability = HistorySearchAvailability.Ready,
                    FailureCode = null,
                    FailureMessage = null,
                });
                return;
            }

            while (page.Count > 0)
            {
                lease.ThrowIfCurrent();
                var results = await _embeddingProviders.GenerateEmbeddingsAsync(
                    resolved,
                    page.Select(static item => item.BodyText).ToArray(),
                    lease.CancellationToken).ConfigureAwait(false);
                lease.ThrowIfCurrent();
                var currentFingerprint = await _embeddingProviders
                    .GetSpaceFingerprintAsync(resolved, lease.CancellationToken)
                    .ConfigureAwait(false);
                lease.ThrowIfCurrent();
                if (!string.Equals(
                        currentFingerprint,
                        configuration.EmbeddingSpaceFingerprint,
                        StringComparison.Ordinal))
                {
                    RequestProviderRefresh();
                    return;
                }
                if (results.Count != page.Count)
                {
                    throw new InvalidOperationException("Embedding provider returned an incomplete batch.");
                }
                for (var index = 0; index < page.Count; index++)
                {
                    lease.ThrowIfCurrent();
                    if (!HistoryVectorCodec.TryNormalize(
                            results[index],
                            configuration.EmbeddingModelId!,
                            dimensions,
                            out _,
                            out var vector))
                    {
                        throw new InvalidOperationException("Embedding provider returned an invalid vector.");
                    }
                    lease.ThrowIfCurrent();
                    _projection.SaveEmbedding(
                        embeddingGenerationId,
                        textGenerationId,
                        configuration,
                        page[index],
                        dimensions,
                        vector);
                }
                page = _projection.ListMissingEmbeddingWorkPage(
                    textGenerationId,
                    embeddingGenerationId,
                    page[^1].DocumentId,
                    EmbeddingBatchSize);
            }
            lease.ThrowIfCurrent();
            _projection.MarkSemanticReadyIfComplete(
                textGenerationId,
                embeddingGenerationId,
                dimensions,
                configuration);
            lease.ThrowIfCurrent();
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Ready,
                FailureCode = null,
                FailureMessage = null,
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            PublishSemanticUnavailable(
                configuration,
                "embedding-backfill-failed",
                "Semantic indexing failed. Lexical search remains available.");
        }
    }
}

internal static class HistoryVectorCodec
{
    internal static bool TryNormalize(
        AgentEmbeddingGenerationResult? result,
        string expectedModelId,
        int? expectedDimensions,
        out int dimensions,
        out byte[] vector)
    {
        dimensions = 0;
        vector = [];
        if (result is null
            || !string.Equals(result.ModelId?.Trim(), expectedModelId.Trim(), StringComparison.Ordinal)
            || result.Values.Count is <= 0 or > HistorySearchLimits.MaximumVectorDimensions
            || expectedDimensions is { } expected && result.Values.Count != expected)
        {
            return false;
        }

        double sum = 0;
        foreach (var value in result.Values)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }
            sum += (double)value * value;
        }
        if (!double.IsFinite(sum) || sum <= 1e-20)
        {
            return false;
        }

        dimensions = result.Values.Count;
        vector = new byte[checked(dimensions * sizeof(float))];
        var scale = 1d / Math.Sqrt(sum);
        for (var index = 0; index < dimensions; index++)
        {
            var normalized = (float)(result.Values[index] * scale);
            if (!float.IsFinite(normalized))
            {
                dimensions = 0;
                vector = [];
                return false;
            }
            BinaryPrimitives.WriteInt32LittleEndian(
                vector.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(normalized));
        }
        return true;
    }

    internal static double Dot(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int dimensions)
    {
        if (dimensions <= 0
            || dimensions > HistorySearchLimits.MaximumVectorDimensions
            || left.Length != checked(dimensions * sizeof(float))
            || right.Length != checked(dimensions * sizeof(float)))
        {
            return double.NegativeInfinity;
        }
        double score = 0;
        for (var index = 0; index < dimensions; index++)
        {
            var offset = index * sizeof(float);
            var leftValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                left.Slice(offset, sizeof(float))));
            var rightValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                right.Slice(offset, sizeof(float))));
            if (!float.IsFinite(leftValue) || !float.IsFinite(rightValue))
            {
                return double.NegativeInfinity;
            }
            score += (double)leftValue * rightValue;
        }
        return double.IsFinite(score) ? score : double.NegativeInfinity;
    }
}

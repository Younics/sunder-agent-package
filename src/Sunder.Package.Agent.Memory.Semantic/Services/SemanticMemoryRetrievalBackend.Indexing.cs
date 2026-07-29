using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed partial class SemanticMemoryRetrievalBackend
{
    private async Task<int> ReindexResolvedSessionAsync(
        Guid sessionId,
        string profileId,
        IReadOnlyList<StoredMemoryRecord> memories,
        ResolvedEmbeddingProvider resolved,
        SemanticIndexingIntent intent,
        CancellationToken cancellationToken)
    {
        if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        var preparedMemories = await PrepareEmbeddingMemoriesAsync(memories, cancellationToken).ConfigureAwait(false);
        var maxCanonicalTextChars = preparedMemories.FirstOrDefault()?.MaxCanonicalTextChars
                                    ?? await _settingsService.GetMaxCanonicalTextCharsAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        var sourceFingerprint = BuildSourceFingerprint(preparedMemories, maxCanonicalTextChars);
        var generationId = _store.GetOrBeginEmbeddingGeneration(
            sessionId,
            resolved.ProviderId,
            resolved.ModelId,
            resolved.ConfigurationFingerprint,
            sourceFingerprint,
            maxCanonicalTextChars,
            preparedMemories.Length);

        var preparedById = preparedMemories.ToDictionary(static memory => memory.Memory.MemoryId);
        var stagedEmbeddings = _store.ListStagedEmbeddings(generationId).ToDictionary(static item => item.Key, static item => item.Value);
        if (stagedEmbeddings.Any(item => !preparedById.TryGetValue(item.Key, out var source)
                                         || !EmbeddingMatches(source, item.Value, resolved))
            || stagedEmbeddings.Values.Select(static embedding => embedding.Dimensions).Distinct().Skip(1).Any())
        {
            _store.AbortEmbeddingGeneration(generationId);
            generationId = _store.GetOrBeginEmbeddingGeneration(
                sessionId,
                resolved.ProviderId,
                resolved.ModelId,
                resolved.ConfigurationFingerprint,
                sourceFingerprint,
                maxCanonicalTextChars,
                preparedMemories.Length);
            stagedEmbeddings.Clear();
        }

        var missingMemories = preparedMemories
            .Where(memory => !stagedEmbeddings.ContainsKey(memory.Memory.MemoryId))
            .ToArray();
        var expectedDimensions = stagedEmbeddings.Values
            .Select(static embedding => (int?)embedding.Dimensions)
            .FirstOrDefault();
        if (!await GenerateEmbeddingsAsync(
                sessionId,
                profileId,
                missingMemories,
                resolved,
                intent,
                expectedDimensions,
                embedding =>
                {
                    var source = preparedById[embedding.MemoryId];
                    if (!_store.TryStageEmbedding(
                            generationId,
                            embedding,
                            source.Memory.MemoryRevision,
                            source.CanonicalTextHash,
                            source.MaxCanonicalTextChars))
                    {
                        throw new InvalidOperationException(
                            $"Memory '{embedding.MemoryId}' changed or was deleted while its embedding was generated.");
                    }
                },
                cancellationToken).ConfigureAwait(false)
            || !await EnsureConfigurationIsCurrentAsync(
                profileId,
                resolved,
                preparedMemories,
                intent,
                cancellationToken).ConfigureAwait(false)
            || !await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _store.CompleteEmbeddingGeneration(generationId);
        return preparedMemories.Length;
    }

    private async Task<int> EnsureEmbeddingsAsync(
        Guid sessionId,
        string profileId,
        IReadOnlyList<PreparedEmbeddingMemory> memories,
        ResolvedEmbeddingProvider resolved,
        IDictionary<Guid, StoredMemoryEmbeddingRecord> existingEmbeddings,
        SemanticIndexingIntent intent,
        CancellationToken cancellationToken)
    {
        var missingMemories = memories
            .Where(memory => !existingEmbeddings.TryGetValue(memory.Memory.MemoryId, out var existing)
                             || !EmbeddingMatches(memory, existing, resolved))
            .ToArray();
        if (missingMemories.Length == 0)
        {
            return 0;
        }

        var expectedDimensions = existingEmbeddings.Values
            .Where(static embedding => embedding.Dimensions > 0)
            .Select(static embedding => (int?)embedding.Dimensions)
            .FirstOrDefault();
        var completed = await GenerateEmbeddingsAsync(
            sessionId,
            profileId,
            missingMemories,
            resolved,
            intent,
            expectedDimensions,
            embedding =>
            {
                var source = missingMemories.First(item => item.Memory.MemoryId == embedding.MemoryId);
                if (!_store.TryUpsertEmbedding(
                        embedding,
                        source.Memory.MemoryRevision,
                        source.CanonicalTextHash,
                        source.MaxCanonicalTextChars,
                        resolved.ConfigurationFingerprint))
                {
                    throw new InvalidOperationException(
                        $"Memory '{embedding.MemoryId}' changed, was deleted, or changed embedding configuration while its embedding was generated.");
                }
                existingEmbeddings[embedding.MemoryId] = embedding;
            },
            cancellationToken).ConfigureAwait(false);
        return completed ? missingMemories.Length : 0;
    }

    private async Task<bool> GenerateEmbeddingsAsync(
        Guid sessionId,
        string profileId,
        IReadOnlyList<PreparedEmbeddingMemory> memories,
        ResolvedEmbeddingProvider resolved,
        SemanticIndexingIntent intent,
        int? expectedDimensions,
        Action<StoredMemoryEmbeddingRecord> persist,
        CancellationToken cancellationToken)
    {
        var batchSize = await _settingsService.GetEmbeddingBatchSizeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var batch in memories.Chunk(batchSize))
        {
            if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            var embeddingResults = await resolved.GenerateEmbeddingsAsync(
                    batch.Select(item => item.CanonicalText).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await EnsureConfigurationIsCurrentAsync(
                    profileId,
                    resolved,
                    batch,
                    intent,
                    cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            if (embeddingResults.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"Embedding provider returned {embeddingResults.Count} result(s) for a batch of {batch.Length}.");
            }

            var validatedResults = new AgentEmbeddingGenerationResult[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                var result = embeddingResults[index];
                if (!IsValidEmbeddingResult(result, resolved.ModelId, expectedDimensions))
                {
                    throw new InvalidOperationException(
                        $"Embedding provider returned an invalid result for memory '{batch[index].Memory.MemoryId}'.");
                }
                expectedDimensions ??= result!.Dimensions;
                validatedResults[index] = result!;
            }

            for (var index = 0; index < batch.Length; index++)
            {
                if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                var result = validatedResults[index];
                var now = DateTimeOffset.UtcNow;
                var existing = _store.GetEmbedding(batch[index].Memory.MemoryId, resolved.ProviderId, resolved.ModelId);
                var embedding = new StoredMemoryEmbeddingRecord(
                    batch[index].Memory.MemoryId,
                    sessionId,
                    resolved.ProviderId,
                    resolved.ModelId,
                    batch[index].CanonicalTextHash,
                    result.Dimensions,
                    result.Values,
                    existing?.CreatedAtUtc ?? now,
                    now)
                {
                    MemoryRevision = batch[index].Memory.MemoryRevision,
                };
                persist(embedding);
            }
        }
        return true;
    }

    private async Task<bool> EnsureConfigurationIsCurrentAsync(
        string profileId,
        ResolvedEmbeddingProvider resolved,
        IReadOnlyList<PreparedEmbeddingMemory> memories,
        SemanticIndexingIntent intent,
        CancellationToken cancellationToken)
    {
        if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        if (!await _modelRuntimeResolver.IsCurrentConfigurationAsync(
                profileId,
                resolved,
                cancellationToken,
                token => IsWorkAllowedAsync(intent, token)).ConfigureAwait(false))
        {
            if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            throw new InvalidOperationException("The embedding provider, model, or provider settings changed during indexing.");
        }

        if (memories.Count > 0
            && await _settingsService.GetMaxCanonicalTextCharsAsync(cancellationToken).ConfigureAwait(false)
            != memories[0].MaxCanonicalTextChars)
        {
            throw new InvalidOperationException("Semantic canonical-text settings changed during indexing.");
        }
        return await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedEmbeddingProvider?> ResolveReadyConfigurationAsync(
        string profileId,
        ResolvedEmbeddingProvider configured,
        SemanticIndexingIntent intent,
        CancellationToken cancellationToken)
    {
        if (!await IsWorkAllowedAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var ready = await _modelRuntimeResolver.ResolveForProfileAsync(
            profileId,
            token => IsWorkAllowedAsync(intent, token),
            cancellationToken).ConfigureAwait(false);
        return ready is not null
               && string.Equals(ready.ProviderId, configured.ProviderId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ready.ModelId, configured.ModelId, StringComparison.Ordinal)
               && string.Equals(
                   ready.ConfigurationFingerprint,
                   configured.ConfigurationFingerprint,
                   StringComparison.Ordinal)
            ? ready
            : null;
    }

    private async Task<bool> IsWorkAllowedAsync(
        SemanticIndexingIntent intent,
        CancellationToken cancellationToken)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        if (intent == SemanticIndexingIntent.ExplicitReindex)
        {
            return true;
        }

        var mode = await _settingsService.GetReindexModeAsync(cancellationToken).ConfigureAwait(false);
        return intent == SemanticIndexingIntent.EagerReconciliation
            ? mode == SemanticReindexMode.Eager
            : mode != SemanticReindexMode.Never;
    }

    private enum SemanticIndexingIntent
    {
        ExplicitReindex,
        EagerReconciliation,
        Recall,
    }
}

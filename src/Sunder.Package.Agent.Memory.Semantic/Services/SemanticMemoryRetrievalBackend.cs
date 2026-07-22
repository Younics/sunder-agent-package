using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Threading;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticMemoryRetrievalBackend(
    MemoryLocalStore store,
    SemanticModelRuntimeResolver modelRuntimeResolver,
    MemorySemanticSettingsService settingsService)
{
    private const int MaxPinnedCandidates = 4;
    internal const int MaxEmbeddingDimensions = 16_384;

    private readonly MemoryLocalStore _store = store;
    private readonly SemanticModelRuntimeResolver _modelRuntimeResolver = modelRuntimeResolver;
    private readonly MemorySemanticSettingsService _settingsService = settingsService;
    private readonly ReferenceCountedKeyedLock<string> _indexLocks = new(StringComparer.OrdinalIgnoreCase);

    internal int IndexLockCount => _indexLocks.Count;

    public async Task IndexMemoryAsync(StoredMemoryRecord memory, string profileId, CancellationToken cancellationToken = default)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken))
        {
            return;
        }

        var resolved = await _modelRuntimeResolver.ResolveForProfileAsync(profileId, cancellationToken);
        if (resolved is null)
        {
            return;
        }

        using (await _indexLocks.EnterAsync(BuildIndexKey(memory.SessionId, resolved), cancellationToken))
        {
            var existingEmbeddings = new Dictionary<Guid, StoredMemoryEmbeddingRecord>();
            var preparedMemory = await PrepareEmbeddingMemoryAsync(memory, cancellationToken);
            await EnsureEmbeddingsAsync(memory.SessionId, [preparedMemory], resolved, existingEmbeddings, allowLazyReindex: true, cancellationToken);
        }
    }

    public async Task<int> ReindexSessionAsync(
        Guid sessionId,
        string profileId,
        IReadOnlyList<StoredMemoryRecord> memories,
        CancellationToken cancellationToken = default)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken))
        {
            return 0;
        }

        var resolved = await _modelRuntimeResolver.ResolveForProfileAsync(profileId, cancellationToken);
        if (resolved is null)
        {
            return 0;
        }

        using (await _indexLocks.EnterAsync(BuildIndexKey(sessionId, resolved), cancellationToken))
        {
            var preparedMemories = await Task.WhenAll(
                memories.Take(MemoryLocalStore.MaxRecallableMemoriesPerSession)
                    .Select(memory => PrepareEmbeddingMemoryAsync(memory, cancellationToken)));
            var generationId = _store.BeginEmbeddingGeneration(
                sessionId,
                resolved.ProviderId,
                resolved.ModelId,
                preparedMemories.Length);
            try
            {
                await GenerateEmbeddingsAsync(
                    sessionId,
                    preparedMemories,
                    resolved,
                    embedding => _store.StageEmbedding(generationId, embedding),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _store.CompleteEmbeddingGeneration(generationId);
                return preparedMemories.Length;
            }
            catch
            {
                _store.AbortEmbeddingGeneration(generationId);
                throw;
            }
        }
    }

    public SemanticMemoryEntryIndexState GetIndexState(
        StoredMemoryRecord memory,
        string providerId,
        string modelId)
    {
        var preparedMemory = PrepareEmbeddingMemoryForStatus(memory);
        var embedding = _store.GetEmbedding(memory.MemoryId, providerId, modelId);
        if (embedding is null)
        {
            return SemanticMemoryEntryIndexState.Missing;
        }

        if (!string.Equals(embedding.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(embedding.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(embedding.CanonicalTextHash, preparedMemory.CanonicalTextHash, StringComparison.Ordinal))
        {
            return SemanticMemoryEntryIndexState.Stale;
        }

        return SemanticMemoryEntryIndexState.Indexed;
    }

    public async Task<IReadOnlyDictionary<Guid, float>> ScoreSemanticAsync(
        string profileId,
        IReadOnlyList<StoredMemoryRecord> memories,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken))
        {
            return new Dictionary<Guid, float>();
        }

        if (memories.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return new Dictionary<Guid, float>();
        }

        var resolved = await _modelRuntimeResolver.ResolveForProfileAsync(profileId, cancellationToken);
        if (resolved is null)
        {
            return new Dictionary<Guid, float>();
        }

        var sessionId = memories[0].SessionId;
        PreparedEmbeddingMemory[] preparedMemories;
        Dictionary<Guid, StoredMemoryEmbeddingRecord> existingEmbeddings;
        using (await _indexLocks.EnterAsync(BuildIndexKey(sessionId, resolved), cancellationToken))
        {
            preparedMemories = await Task.WhenAll(
                memories.Take(MemoryLocalStore.MaxRecallableMemoriesPerSession)
                    .Select(memory => PrepareEmbeddingMemoryAsync(memory, cancellationToken)));
            existingEmbeddings = _store.ListEmbeddings(sessionId, resolved.ProviderId, resolved.ModelId)
                .ToDictionary(item => item.Key, item => item.Value);

            await EnsureEmbeddingsAsync(sessionId, preparedMemories, resolved, existingEmbeddings, allowLazyReindex: false, cancellationToken);
        }

        var queryEmbedding = await resolved.Provider.GenerateEmbeddingAsync(resolved.ModelId, query.Trim(), cancellationToken);
        if (queryEmbedding is null || queryEmbedding.Values.Count == 0)
        {
            return new Dictionary<Guid, float>();
        }

        var scores = new Dictionary<Guid, float>();
        foreach (var preparedMemory in preparedMemories)
        {
            if (!existingEmbeddings.TryGetValue(preparedMemory.Memory.MemoryId, out var embedding)
                || !string.Equals(embedding.CanonicalTextHash, preparedMemory.CanonicalTextHash, StringComparison.Ordinal)
                || embedding.Values.Count != queryEmbedding.Values.Count)
            {
                continue;
            }

            var similarity = CalculateCosineSimilarity(queryEmbedding.Values, embedding.Values);
            if (similarity > 0f)
            {
                scores[preparedMemory.Memory.MemoryId] = similarity;
            }
        }

        return scores;
    }

    public async Task<HybridRecallCandidateSet> BuildRecallCandidatesAsync(
        string profileId,
        Guid sessionId,
        AgentMemoryRecallPlan recallPlan,
        string query,
        CancellationToken cancellationToken = default)
    {
        var activeMemories = _store.ListRecallableMemories(sessionId)
            .Where(memory => recallPlan.PreferredCategories is not { Count: > 0 }
                             || memory.IsPinned
                             || recallPlan.PreferredCategories.Any(category => string.Equals(category, memory.Category, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (activeMemories.Length == 0)
        {
            return new HybridRecallCandidateSet([], new Dictionary<Guid, float>(), new Dictionary<Guid, float>());
        }

        var lexicalHits = _store.SearchMemories(
            sessionId,
            query,
            recallPlan.PreferredCategories,
            includeInactive: false,
            limit: Math.Max(recallPlan.MaxEntryCount * 4, 12));
        var lexicalScores = lexicalHits
            .GroupBy(result => result.Memory.MemoryId)
            .ToDictionary(group => group.Key, group => NormalizeTextSearchScore(group.Min(item => item.SearchRank)));

        var semanticScores = await ScoreSemanticAsync(profileId, activeMemories, query, cancellationToken);
        var semanticCandidateIds = semanticScores
            .OrderByDescending(item => item.Value)
            .Take(Math.Max(recallPlan.MaxEntryCount * 4, 12))
            .Select(item => item.Key)
            .ToHashSet();

        var candidateIds = new HashSet<Guid>(lexicalScores.Keys);
        foreach (var memoryId in semanticCandidateIds)
        {
            candidateIds.Add(memoryId);
        }

        foreach (var memory in activeMemories.Where(memory => memory.IsPinned).Take(MaxPinnedCandidates))
        {
            candidateIds.Add(memory.MemoryId);
        }

        if (candidateIds.Count == 0)
        {
            foreach (var memory in _store.ListPriorityMemories(sessionId, limit: recallPlan.MaxEntryCount))
            {
                candidateIds.Add(memory.MemoryId);
            }
        }

        var candidates = activeMemories
            .Where(memory => candidateIds.Contains(memory.MemoryId))
            .ToArray();
        return new HybridRecallCandidateSet(candidates, lexicalScores, semanticScores);
    }

    private async Task EnsureEmbeddingsAsync(
        Guid sessionId,
        IReadOnlyList<PreparedEmbeddingMemory> memories,
        ResolvedEmbeddingProvider resolved,
        IDictionary<Guid, StoredMemoryEmbeddingRecord> existingEmbeddings,
        bool allowLazyReindex,
        CancellationToken cancellationToken)
    {
        var missingMemories = memories
            .Where(memory => !existingEmbeddings.TryGetValue(memory.Memory.MemoryId, out var existing)
                             || !string.Equals(existing.ProviderId, resolved.ProviderId, StringComparison.OrdinalIgnoreCase)
                             || !string.Equals(existing.ModelId, resolved.ModelId, StringComparison.OrdinalIgnoreCase)
                             || !string.Equals(existing.CanonicalTextHash, memory.CanonicalTextHash, StringComparison.Ordinal))
            .ToArray();

        if (!allowLazyReindex
            && await _settingsService.GetReindexModeAsync(cancellationToken) == SemanticReindexMode.Never)
        {
            return;
        }

        await GenerateEmbeddingsAsync(
            sessionId,
            missingMemories,
            resolved,
            embedding =>
            {
                _store.UpsertEmbedding(embedding);
                existingEmbeddings[embedding.MemoryId] = embedding;
            },
            cancellationToken);
    }

    private async Task GenerateEmbeddingsAsync(
        Guid sessionId,
        IReadOnlyList<PreparedEmbeddingMemory> memories,
        ResolvedEmbeddingProvider resolved,
        Action<StoredMemoryEmbeddingRecord> persist,
        CancellationToken cancellationToken)
    {
        foreach (var batch in memories.Chunk(await _settingsService.GetEmbeddingBatchSizeAsync(cancellationToken)))
        {
            var embeddingResults = await resolved.Provider.GenerateEmbeddingsAsync(
                resolved.ModelId,
                batch.Select(item => item.CanonicalText).ToArray(),
                cancellationToken);

            for (var index = 0; index < batch.Length && index < embeddingResults.Count; index++)
            {
                var result = embeddingResults[index];
                if (result is null
                    || result.Values.Count == 0
                    || result.Values.Count > MaxEmbeddingDimensions
                    || result.Dimensions != result.Values.Count)
                {
                    continue;
                }

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
                    now);
                persist(embedding);
            }
        }
    }

    private async Task<PreparedEmbeddingMemory> PrepareEmbeddingMemoryAsync(
        StoredMemoryRecord memory,
        CancellationToken cancellationToken)
    {
        var canonicalText = await BuildCanonicalTextAsync(memory, cancellationToken);
        return new PreparedEmbeddingMemory(memory, canonicalText, ComputeCanonicalTextHash(canonicalText));
    }

    private PreparedEmbeddingMemory PrepareEmbeddingMemoryForStatus(StoredMemoryRecord memory)
    {
        var canonicalText = BuildCanonicalText(memory, _settingsService.CachedMaxCanonicalTextChars);
        return new PreparedEmbeddingMemory(memory, canonicalText, ComputeCanonicalTextHash(canonicalText));
    }

    private async Task<string> BuildCanonicalTextAsync(
        StoredMemoryRecord memory,
        CancellationToken cancellationToken)
    {
        var maxLength = await _settingsService.GetMaxCanonicalTextCharsAsync(cancellationToken);
        return BuildCanonicalText(memory, maxLength);
    }

    private static string BuildCanonicalText(StoredMemoryRecord memory, int maxLength)
    {
        var builder = new StringBuilder();
        builder.Append("Category: ").AppendLine(memory.Category);
        builder.Append("Content: ").AppendLine(memory.Content);
        if (!string.IsNullOrWhiteSpace(memory.EvidenceText))
        {
            builder.Append("Evidence: ").AppendLine(memory.EvidenceText.Trim());
        }

        builder.Append("Trust: ").Append(memory.State);
        var canonicalText = builder.ToString().Trim();
        return canonicalText.Length <= maxLength
            ? canonicalText
            : canonicalText[..maxLength].TrimEnd();
    }

    private static string ComputeCanonicalTextHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static string BuildIndexKey(Guid sessionId, ResolvedEmbeddingProvider resolved)
        => $"{sessionId:N}\0{resolved.ProviderId}\0{resolved.ModelId}";

    private static float CalculateCosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || right.Count == 0 || left.Count != right.Count)
        {
            return 0f;
        }

        double dot = 0d;
        double leftMagnitude = 0d;
        double rightMagnitude = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude <= 0d || rightMagnitude <= 0d)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude)));
    }

    private static float NormalizeTextSearchScore(double searchRank)
    {
        var magnitude = Math.Abs(searchRank);
        return magnitude <= 0d ? 1f : (float)(1d / (1d + magnitude));
    }
}

public sealed record PreparedEmbeddingMemory(
    StoredMemoryRecord Memory,
    string CanonicalText,
    string CanonicalTextHash);

public enum SemanticMemoryEntryIndexState
{
    Missing = 0,
    Stale = 1,
    Indexed = 2,
}

public sealed record HybridRecallCandidateSet(
    IReadOnlyList<StoredMemoryRecord> Memories,
    IReadOnlyDictionary<Guid, float> TextSearchScores,
    IReadOnlyDictionary<Guid, float> SemanticScores);

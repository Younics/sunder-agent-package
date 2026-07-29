using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Threading;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed partial class SemanticMemoryRetrievalBackend(
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

    public Task IndexMemoryAsync(
        StoredMemoryRecord memory,
        string profileId,
        CancellationToken cancellationToken = default)
        => ReconcileSessionAsync(memory.SessionId, profileId, cancellationToken);

    public async Task<int> ReindexSessionAsync(
        Guid sessionId,
        string profileId,
        IReadOnlyList<StoredMemoryRecord> memories,
        CancellationToken cancellationToken = default)
    {
        if (!await IsWorkAllowedAsync(SemanticIndexingIntent.ExplicitReindex, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        if (_store.HasSessionDeletionTombstone(sessionId))
        {
            _store.DeleteEmbeddings(sessionId);
            return 0;
        }

        var resolved = await _modelRuntimeResolver.ResolveForProfileAsync(
            profileId,
            token => IsWorkAllowedAsync(SemanticIndexingIntent.ExplicitReindex, token),
            cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return 0;
        }

        using (await _indexLocks.EnterAsync(BuildIndexKey(sessionId), cancellationToken))
        {
            return await ReindexResolvedSessionAsync(
                sessionId,
                profileId,
                FilterRecallableMemories(sessionId, memories),
                resolved,
                SemanticIndexingIntent.ExplicitReindex,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<SemanticMemoryReconciliationRequirement> GetReconciliationRequirementAsync(
        Guid sessionId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var memories = _store.ListMemories(sessionId, includeInactive: false);
        var retainedMemoryIds = memories.Select(static memory => memory.MemoryId).ToHashSet();
        var activeGenerations = _store.ListActiveEmbeddingGenerations(sessionId);
        var hasRetractions = _store.HasEmbeddingRetractions(sessionId, retainedMemoryIds);

        if (_store.HasSessionDeletionTombstone(sessionId) || memories.Count == 0)
        {
            return activeGenerations.Count == 0
                ? SemanticMemoryReconciliationRequirement.None
                : new(true, HasRetractions: true);
        }

        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return hasRetractions
                ? new(true, HasRetractions: true)
                : SemanticMemoryReconciliationRequirement.None;
        }

        if (await _settingsService.GetReindexModeAsync(cancellationToken).ConfigureAwait(false)
            != SemanticReindexMode.Eager)
        {
            return hasRetractions
                ? new(true, HasRetractions: true)
                : SemanticMemoryReconciliationRequirement.None;
        }

        var resolved = await _modelRuntimeResolver
            .ResolveConfigurationForProfileAsync(
                profileId,
                cancellationToken,
                canInvokeProvider: token => IsWorkAllowedAsync(SemanticIndexingIntent.EagerReconciliation, token))
            .ConfigureAwait(false);
        if (resolved is null)
        {
            return hasRetractions
                ? new(true, HasRetractions: true)
                : SemanticMemoryReconciliationRequirement.None;
        }

        var selectedGeneration = _store.GetActiveEmbeddingGeneration(
            sessionId,
            resolved.ProviderId,
            resolved.ModelId);
        var requiresEmbeddingWork = !GenerationMatches(selectedGeneration, resolved);
        if (!requiresEmbeddingWork)
        {
            var preparedMemories = await PrepareEmbeddingMemoriesAsync(memories, cancellationToken).ConfigureAwait(false);
            var existingEmbeddings = _store.ListEmbeddings(sessionId, resolved.ProviderId, resolved.ModelId);
            requiresEmbeddingWork = preparedMemories.Any(memory =>
                !existingEmbeddings.TryGetValue(memory.Memory.MemoryId, out var embedding)
                || !EmbeddingMatches(memory, embedding, resolved));
        }

        return hasRetractions || requiresEmbeddingWork
            ? new(true, hasRetractions)
            : SemanticMemoryReconciliationRequirement.None;
    }

    internal async Task<int> ReconcileSessionAsync(
        Guid sessionId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        using (await _indexLocks.EnterAsync(BuildIndexKey(sessionId), cancellationToken))
        {
            if (_store.HasSessionDeletionTombstone(sessionId))
            {
                _store.DeleteEmbeddings(sessionId);
                return 0;
            }

            var memories = _store.ListMemories(sessionId, includeInactive: false);
            var retainedMemoryIds = memories.Select(static memory => memory.MemoryId).ToHashSet();
            _store.PruneEmbeddingGenerations(sessionId, retainedMemoryIds);

            if (memories.Count == 0)
            {
                _store.DeleteEmbeddings(sessionId);
                return 0;
            }

            if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken).ConfigureAwait(false)
                || await _settingsService.GetReindexModeAsync(cancellationToken).ConfigureAwait(false)
                != SemanticReindexMode.Eager)
            {
                return 0;
            }

            var configured = await _modelRuntimeResolver
                .ResolveConfigurationForProfileAsync(
                    profileId,
                    cancellationToken,
                    canInvokeProvider: token => IsWorkAllowedAsync(SemanticIndexingIntent.EagerReconciliation, token))
                .ConfigureAwait(false);
            if (configured is null)
            {
                return 0;
            }

            var resolved = await ResolveReadyConfigurationAsync(
                profileId,
                configured,
                SemanticIndexingIntent.EagerReconciliation,
                cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return 0;
            }

            var selectedGeneration = _store.GetActiveEmbeddingGeneration(
                sessionId,
                resolved.ProviderId,
                resolved.ModelId);
            if (!GenerationMatches(selectedGeneration, resolved))
            {
                return await ReindexResolvedSessionAsync(
                    sessionId,
                    profileId,
                    memories,
                    resolved,
                    SemanticIndexingIntent.EagerReconciliation,
                    cancellationToken).ConfigureAwait(false);
            }

            var preparedMemories = await PrepareEmbeddingMemoriesAsync(memories, cancellationToken).ConfigureAwait(false);
            var existingEmbeddings = _store.ListEmbeddings(sessionId, resolved.ProviderId, resolved.ModelId)
                .ToDictionary(static item => item.Key, static item => item.Value);
            return await EnsureEmbeddingsAsync(
                sessionId,
                profileId,
                preparedMemories,
                resolved,
                existingEmbeddings,
                SemanticIndexingIntent.EagerReconciliation,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public SemanticMemoryEntryIndexState GetIndexState(
        StoredMemoryRecord memory,
        string providerId,
        string modelId,
        string? configurationFingerprint = null)
    {
        if (configurationFingerprint is not null)
        {
            var generation = _store.GetActiveEmbeddingGeneration(memory.SessionId, providerId, modelId);
            if (generation is null
                || !string.Equals(
                    generation.ConfigurationFingerprint,
                    configurationFingerprint,
                    StringComparison.Ordinal))
            {
                return SemanticMemoryEntryIndexState.Stale;
            }
        }

        var preparedMemory = PrepareEmbeddingMemory(memory, _settingsService.CachedMaxCanonicalTextChars);
        var embedding = _store.GetEmbedding(memory.MemoryId, providerId, modelId);
        if (embedding is null)
        {
            return SemanticMemoryEntryIndexState.Missing;
        }

        if (!string.Equals(embedding.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(embedding.ModelId, modelId, StringComparison.Ordinal)
            || !string.Equals(embedding.CanonicalTextHash, preparedMemory.CanonicalTextHash, StringComparison.Ordinal)
            || embedding.MemoryRevision != memory.MemoryRevision)
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
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken).ConfigureAwait(false)
            || memories.Count == 0
            || string.IsNullOrWhiteSpace(query))
        {
            return new Dictionary<Guid, float>();
        }

        var resolved = await _modelRuntimeResolver.ResolveForProfileAsync(
            profileId,
            token => IsWorkAllowedAsync(SemanticIndexingIntent.Recall, token),
            cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return new Dictionary<Guid, float>();
        }

        var sessionId = memories[0].SessionId;
        PreparedEmbeddingMemory[] preparedMemories;
        Dictionary<Guid, StoredMemoryEmbeddingRecord> existingEmbeddings;
        using (await _indexLocks.EnterAsync(BuildIndexKey(sessionId), cancellationToken))
        {
            var recallableMemories = FilterRecallableMemories(sessionId, memories);
            preparedMemories = await PrepareEmbeddingMemoriesAsync(recallableMemories, cancellationToken).ConfigureAwait(false);
            var selectedGeneration = _store.GetActiveEmbeddingGeneration(
                sessionId,
                resolved.ProviderId,
                resolved.ModelId);
            var reindexMode = await _settingsService.GetReindexModeAsync(cancellationToken).ConfigureAwait(false);
            if (!GenerationMatches(selectedGeneration, resolved))
            {
                if (reindexMode == SemanticReindexMode.Never)
                {
                    return new Dictionary<Guid, float>();
                }

                await ReindexResolvedSessionAsync(
                    sessionId,
                    profileId,
                    recallableMemories,
                    resolved,
                    SemanticIndexingIntent.Recall,
                    cancellationToken).ConfigureAwait(false);
            }

            existingEmbeddings = _store.ListEmbeddings(sessionId, resolved.ProviderId, resolved.ModelId)
                .ToDictionary(static item => item.Key, static item => item.Value);
            if (reindexMode != SemanticReindexMode.Never)
            {
                await EnsureEmbeddingsAsync(
                    sessionId,
                    profileId,
                    preparedMemories,
                    resolved,
                    existingEmbeddings,
                    SemanticIndexingIntent.Recall,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!preparedMemories.Any(memory =>
                    existingEmbeddings.TryGetValue(memory.Memory.MemoryId, out var embedding)
                    && EmbeddingMatches(memory, embedding, resolved)))
            {
                return new Dictionary<Guid, float>();
            }
        }

        if (!await IsWorkAllowedAsync(SemanticIndexingIntent.Recall, cancellationToken).ConfigureAwait(false))
        {
            return new Dictionary<Guid, float>();
        }
        var queryEmbedding = await resolved.GenerateEmbeddingAsync(query.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (!await IsWorkAllowedAsync(SemanticIndexingIntent.Recall, cancellationToken).ConfigureAwait(false)
            || !IsValidEmbeddingResult(queryEmbedding, resolved.ModelId, expectedDimensions: null))
        {
            return new Dictionary<Guid, float>();
        }

        var scores = new Dictionary<Guid, float>();
        foreach (var preparedMemory in preparedMemories)
        {
            if (!existingEmbeddings.TryGetValue(preparedMemory.Memory.MemoryId, out var embedding)
                || !EmbeddingMatches(preparedMemory, embedding, resolved)
                || embedding.Values.Count != queryEmbedding!.Values.Count)
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

        var semanticScores = await ScoreSemanticAsync(profileId, activeMemories, query, cancellationToken).ConfigureAwait(false);
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

    private async Task<PreparedEmbeddingMemory[]> PrepareEmbeddingMemoriesAsync(
        IReadOnlyList<StoredMemoryRecord> memories,
        CancellationToken cancellationToken)
    {
        var maxLength = await _settingsService.GetMaxCanonicalTextCharsAsync(cancellationToken).ConfigureAwait(false);
        return memories
            .Take(MemoryLocalStore.MaxRecallableMemoriesPerSession)
            .Select(memory => PrepareEmbeddingMemory(memory, maxLength))
            .ToArray();
    }

    private static PreparedEmbeddingMemory PrepareEmbeddingMemory(StoredMemoryRecord memory, int maxLength)
    {
        var canonicalText = BuildCanonicalText(memory, maxLength);
        return new PreparedEmbeddingMemory(memory, canonicalText, ComputeCanonicalTextHash(canonicalText))
        {
            MaxCanonicalTextChars = maxLength,
        };
    }

    private static bool GenerationMatches(
        StoredMemoryEmbeddingGenerationRecord? generation,
        ResolvedEmbeddingProvider resolved)
        => generation is not null
           && string.Equals(generation.ProviderId, resolved.ProviderId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(generation.ModelId, resolved.ModelId, StringComparison.Ordinal)
           && string.Equals(
               generation.ConfigurationFingerprint,
               resolved.ConfigurationFingerprint,
               StringComparison.Ordinal);

    private static bool EmbeddingMatches(
        PreparedEmbeddingMemory memory,
        StoredMemoryEmbeddingRecord embedding,
        ResolvedEmbeddingProvider resolved)
        => string.Equals(embedding.ProviderId, resolved.ProviderId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(embedding.ModelId, resolved.ModelId, StringComparison.Ordinal)
           && string.Equals(embedding.CanonicalTextHash, memory.CanonicalTextHash, StringComparison.Ordinal)
           && embedding.MemoryRevision == memory.Memory.MemoryRevision
           && embedding.Dimensions is > 0 and <= MaxEmbeddingDimensions
           && embedding.Values.Count == embedding.Dimensions
           && embedding.Values.All(float.IsFinite);

    private static bool IsValidEmbeddingResult(
        AgentEmbeddingGenerationResult? result,
        string expectedModelId,
        int? expectedDimensions)
        => result is not null
           && string.Equals(result.ModelId, expectedModelId, StringComparison.Ordinal)
           && result.Values.Count is > 0 and <= MaxEmbeddingDimensions
           && (expectedDimensions is null || result.Values.Count == expectedDimensions)
           && result.Values.All(float.IsFinite);

    private static IReadOnlyList<StoredMemoryRecord> FilterRecallableMemories(
        Guid sessionId,
        IReadOnlyList<StoredMemoryRecord> memories)
        => memories
            .Where(memory => memory.SessionId == sessionId
                             && (string.Equals(memory.State, MemoryLocalStore.ActiveState, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(memory.State, MemoryLocalStore.ContestedState, StringComparison.OrdinalIgnoreCase)))
            .Take(MemoryLocalStore.MaxRecallableMemoriesPerSession)
            .ToArray();

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

    private static string BuildSourceFingerprint(
        IReadOnlyList<PreparedEmbeddingMemory> memories,
        int maxCanonicalTextChars)
    {
        var builder = new StringBuilder("semantic-memory-generation-source-v1\n");
        builder.Append(maxCanonicalTextChars).Append('\n');
        foreach (var memory in memories.OrderBy(static item => item.Memory.MemoryId))
        {
            builder.Append(memory.Memory.MemoryId.ToString("N"))
                .Append(':')
                .Append(memory.Memory.MemoryRevision)
                .Append(':')
                .Append(memory.CanonicalTextHash)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string BuildIndexKey(Guid sessionId) => sessionId.ToString("N");

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

internal sealed record SemanticMemoryReconciliationRequirement(bool ShouldQueue, bool HasRetractions)
{
    public static SemanticMemoryReconciliationRequirement None { get; } = new(false, false);
}

public sealed record PreparedEmbeddingMemory(
    StoredMemoryRecord Memory,
    string CanonicalText,
    string CanonicalTextHash)
{
    public int MaxCanonicalTextChars { get; init; }
}

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

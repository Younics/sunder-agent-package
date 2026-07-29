using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Threading;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore
{
    public const int MaxRecallableMemoriesPerSession = 512;
    public const string ActiveState = "Active";
    public const string ContestedState = "Contested";
    public const string ForgottenState = "Forgotten";
    public const string SupersededState = "Superseded";

    private static readonly ReferenceCountedKeyedLock<string> MemoryWriteLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly MemoryRepository _memories;
    private readonly EvidenceRepository _evidence;
    private readonly EmbeddingRepository _embeddings;
    private readonly IMemoryPhysicalMaintenance _physicalMaintenance;

    public MemoryLocalStore(IPackageContext packageContext)
        : this(packageContext, SqliteMemoryPhysicalMaintenance.Instance)
    {
    }

    internal MemoryLocalStore(
        IPackageContext packageContext,
        IMemoryPhysicalMaintenance physicalMaintenance)
    {
        ArgumentNullException.ThrowIfNull(physicalMaintenance);
        MemoryDatabase.Initialize(packageContext.ContentRootPath);
        DatabasePath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("memory/agent-memory.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        new MemorySchemaMigrator(DatabasePath).Migrate();
        _evidence = new EvidenceRepository(DatabasePath);
        _embeddings = new EmbeddingRepository(DatabasePath);
        _memories = new MemoryRepository(DatabasePath, _evidence);
        _physicalMaintenance = physicalMaintenance;
    }

    public string DatabasePath { get; }

    public IReadOnlyList<StoredMemoryRecord> ListActiveMemories(Guid sessionId) => _memories.ListActive(sessionId);

    public IReadOnlyList<StoredMemoryRecord> ListRecallableMemories(Guid sessionId) => _memories.ListRecallable(sessionId);

    public IReadOnlyList<StoredMemoryRecord> ListPriorityMemories(Guid sessionId, int limit) => _memories.ListPriority(sessionId, limit);

    public StoredMemoryRecord UpsertMemory(MemoryUpsertRequest request, Guid? targetMemoryId = null) => _memories.Upsert(request, targetMemoryId);

    internal StoredMemoryRecord? TryUpsertMemoryWithinLimit(MemoryUpsertRequest request, Guid? targetMemoryId = null)
    {
        using (MemoryWriteLocks.Enter($"{DatabasePath}\0{request.SessionId:N}"))
        {
            if (targetMemoryId is null
                && _memories.CountRecallable(request.SessionId) >= MaxRecallableMemoriesPerSession)
            {
                return null;
            }

            return _memories.Upsert(request, targetMemoryId);
        }
    }

    public void RecordRecall(IReadOnlyList<Guid> memoryIds) => _memories.RecordRecall(memoryIds);

    public IReadOnlyList<StoredMemoryRecord> ListMemories(Guid sessionId, string? searchText = null, bool includeInactive = false)
        => _memories.List(sessionId, searchText, includeInactive);

    public IReadOnlyList<StoredMemorySearchResult> SearchMemories(
        Guid sessionId,
        string searchText,
        IReadOnlyList<string>? preferredCategories,
        bool includeInactive,
        int limit)
        => _memories.Search(sessionId, searchText, preferredCategories, includeInactive, limit);

    public StoredMemoryRecord? GetMemory(Guid memoryId) => _memories.Get(memoryId);

    public IReadOnlyList<StoredMemoryEvidenceRecord> ListEvidence(Guid memoryId) => _evidence.List(memoryId);

    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned, string? note = null) => _memories.SetPinned(memoryId, isPinned, note);

    public StoredMemoryRecord UpdateMemory(Guid memoryId, string category, string content, string? note)
        => _memories.Update(memoryId, category, content, note);

    public StoredMemoryRecord SetContested(Guid memoryId, string? note = null)
        => SetState(memoryId, ContestedState, note ?? "Marked as contested in memory inspector.");

    public MemoryCorrectionResult CreateCorrectedMemory(Guid sourceMemoryId, string category, string content, string? note)
        => _memories.CreateCorrection(sourceMemoryId, category, content, note);

    public StoredMemoryRecord SetState(Guid memoryId, string state, string? note) => _memories.SetState(memoryId, state, note);

    public StoredMemoryRecord? GetSupersedingMemory(Guid memoryId) => _memories.GetSuperseding(memoryId);

    public IReadOnlyList<StoredMemoryRecord> ListSupersededMemories(Guid supersedingMemoryId) => _memories.ListSuperseded(supersedingMemoryId);

    public IReadOnlyList<StoredMemoryRecord> ListCorrectionLineage(Guid memoryId)
    {
        var related = new List<StoredMemoryRecord>();
        var visited = new HashSet<Guid> { memoryId };
        var queue = new Queue<Guid>();
        queue.Enqueue(memoryId);
        while (queue.TryDequeue(out var currentId))
        {
            if (GetSupersedingMemory(currentId) is { } superseding && visited.Add(superseding.MemoryId))
            {
                related.Add(superseding);
                queue.Enqueue(superseding.MemoryId);
            }

            foreach (var superseded in ListSupersededMemories(currentId).Where(item => visited.Add(item.MemoryId)))
            {
                related.Add(superseded);
                queue.Enqueue(superseded.MemoryId);
            }
        }

        return related.OrderByDescending(memory => memory.UpdatedAtUtc).ToArray();
    }

    public StoredMemoryEmbeddingRecord? GetEmbedding(Guid memoryId) => _embeddings.Get(memoryId);

    internal StoredMemoryEmbeddingRecord? GetEmbedding(Guid memoryId, string providerId, string modelId)
        => _embeddings.Get(memoryId, providerId, modelId);

    public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> ListEmbeddings(Guid sessionId, string providerId, string modelId)
        => _embeddings.List(sessionId, providerId, modelId);

    internal IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> ListStagedEmbeddings(string generationId)
        => _embeddings.ListGeneration(generationId);

    internal StoredMemoryEmbeddingGenerationRecord? GetActiveEmbeddingGeneration(
        Guid sessionId,
        string providerId,
        string modelId)
        => _embeddings.GetActiveGeneration(sessionId, providerId, modelId);

    internal IReadOnlyList<StoredMemoryEmbeddingGenerationRecord> ListActiveEmbeddingGenerations(Guid sessionId)
        => _embeddings.ListActiveGenerations(sessionId);

    internal bool HasEmbeddingRetractions(Guid sessionId, IReadOnlySet<Guid> retainedMemoryIds)
        => _embeddings.HasRetractions(sessionId, retainedMemoryIds);

    public void UpsertEmbedding(StoredMemoryEmbeddingRecord embedding) => _embeddings.Upsert(embedding);

    internal bool TryUpsertEmbedding(
        StoredMemoryEmbeddingRecord embedding,
        long expectedMemoryRevision,
        string expectedCanonicalTextHash,
        int maxCanonicalTextChars,
        string configurationFingerprint = "")
        => _embeddings.TryUpsertFenced(
            embedding,
            expectedMemoryRevision,
            expectedCanonicalTextHash,
            maxCanonicalTextChars,
            configurationFingerprint);

    internal string GetOrBeginEmbeddingGeneration(
        Guid sessionId,
        string providerId,
        string modelId,
        string configurationFingerprint,
        string sourceFingerprint,
        int maxCanonicalTextChars,
        int expectedMemoryCount)
        => _embeddings.GetOrBeginGeneration(
            sessionId,
            providerId,
            modelId,
            configurationFingerprint,
            sourceFingerprint,
            maxCanonicalTextChars,
            expectedMemoryCount);

    internal bool TryStageEmbedding(
        string generationId,
        StoredMemoryEmbeddingRecord embedding,
        long expectedMemoryRevision,
        string expectedCanonicalTextHash,
        int maxCanonicalTextChars)
        => _embeddings.TryStageFenced(
            generationId,
            embedding,
            expectedMemoryRevision,
            expectedCanonicalTextHash,
            maxCanonicalTextChars);

    internal void CompleteEmbeddingGeneration(string generationId) => _embeddings.CompleteGeneration(generationId);

    internal void AbortEmbeddingGeneration(string generationId) => _embeddings.AbortGeneration(generationId);

    internal int PruneEmbeddingGeneration(string generationId, IReadOnlySet<Guid> retainedMemoryIds)
        => _embeddings.PruneGeneration(generationId, retainedMemoryIds);

    internal int PruneEmbeddingGenerations(Guid sessionId, IReadOnlySet<Guid> retainedMemoryIds)
        => _embeddings.PruneSessionGenerations(sessionId, retainedMemoryIds);

    public void DeleteEmbeddings(Guid sessionId) => _embeddings.DeleteSession(sessionId);

    public void DeleteSessionData(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(DatabasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        using (var tombstone = connection.CreateCommand())
        {
            var receiptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"semantic-memory-session-cleaner-v1\n{sessionId:N}"))).ToLowerInvariant();
            tombstone.Transaction = transaction;
            tombstone.CommandText = """
                INSERT OR IGNORE INTO SessionMemoryDeletionTombstones (
                    SessionId, WorkspaceId, EventId, PayloadHash, DeletedAtUtc)
                VALUES ($sessionId, NULL, $eventId, $payloadHash, $deletedAtUtc);
                """;
            tombstone.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            tombstone.Parameters.AddWithValue("$eventId", $"session-cleaner_{receiptHash}");
            tombstone.Parameters.AddWithValue("$payloadHash", receiptHash);
            tombstone.Parameters.AddWithValue("$deletedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            var affected = tombstone.ExecuteNonQuery();
            if (affected is not (0 or 1))
            {
                throw new InvalidOperationException(
                    $"Session deletion tombstone for '{sessionId}' could not be persisted.");
            }
        }
        EmbeddingRepository.DeleteSession(connection, transaction, sessionId);
        EvidenceRepository.DeleteSession(connection, transaction, sessionId);
        using (var contributions = connection.CreateCommand())
        {
            contributions.Transaction = transaction;
            contributions.CommandText = "DELETE FROM SessionMemoryContributions WHERE SessionId = $sessionId;";
            contributions.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            contributions.ExecuteNonQuery();
        }
        MemoryRepository.DeleteSession(connection, transaction, sessionId);
        transaction.Commit();
        using var maintenanceLock = MemoryDatabase.AcquireMaintenanceLock(DatabasePath);
        _physicalMaintenance.SecurePurge(connection);
    }

    internal bool HasLifecycleInboxReceipt(AgentMemoryConsistencyBarrier barrier)
    {
        using var connection = MemoryDatabase.OpenConnection(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SemanticLifecycleInbox WHERE EventId = $eventId AND PayloadHash = $payloadHash LIMIT 1;";
        command.Parameters.AddWithValue("$eventId", barrier.EventId);
        command.Parameters.AddWithValue("$payloadHash", barrier.PayloadHash);
        return command.ExecuteScalar() is not null;
    }

    internal bool HasSessionDeletionTombstone(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SessionMemoryDeletionTombstones WHERE SessionId = $sessionId LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return command.ExecuteScalar() is not null;
    }

    private static string CreateConnectionString(string databasePath) => MemoryDatabase.CreateConnectionString(databasePath);
}

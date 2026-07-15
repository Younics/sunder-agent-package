using Microsoft.Data.Sqlite;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class MemoryLocalStore
{
    public const string ActiveState = "Active";
    public const string ContestedState = "Contested";
    public const string ForgottenState = "Forgotten";
    public const string SupersededState = "Superseded";

    private readonly MemoryRepository _memories;
    private readonly EvidenceRepository _evidence;
    private readonly EmbeddingRepository _embeddings;

    public MemoryLocalStore(IPackageContext packageContext)
    {
        MemoryDatabase.Initialize(packageContext.ContentRootPath);
        DatabasePath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("memory/agent-memory.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        new MemorySchemaMigrator(DatabasePath).Migrate();
        _evidence = new EvidenceRepository(DatabasePath);
        _embeddings = new EmbeddingRepository(DatabasePath);
        _memories = new MemoryRepository(DatabasePath, _evidence);
        _embeddings.CleanupStagingGenerations();
    }

    public string DatabasePath { get; }

    public IReadOnlyList<StoredMemoryRecord> ListActiveMemories(Guid sessionId) => _memories.ListActive(sessionId);

    public IReadOnlyList<StoredMemoryRecord> ListRecallableMemories(Guid sessionId) => _memories.ListRecallable(sessionId);

    public IReadOnlyList<StoredMemoryRecord> ListPriorityMemories(Guid sessionId, int limit) => _memories.ListPriority(sessionId, limit);

    public StoredMemoryRecord UpsertMemory(MemoryUpsertRequest request, Guid? targetMemoryId = null) => _memories.Upsert(request, targetMemoryId);

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

    public void UpsertEmbedding(StoredMemoryEmbeddingRecord embedding) => _embeddings.Upsert(embedding);

    internal string BeginEmbeddingGeneration(Guid sessionId, string providerId, string modelId, int expectedMemoryCount)
        => _embeddings.BeginGeneration(sessionId, providerId, modelId, expectedMemoryCount);

    internal void StageEmbedding(string generationId, StoredMemoryEmbeddingRecord embedding) => _embeddings.Stage(generationId, embedding);

    internal void CompleteEmbeddingGeneration(string generationId) => _embeddings.CompleteGeneration(generationId);

    internal void AbortEmbeddingGeneration(string generationId) => _embeddings.AbortGeneration(generationId);

    internal int CleanupStagingEmbeddingGenerations() => _embeddings.CleanupStagingGenerations();

    public void DeleteEmbeddings(Guid sessionId) => _embeddings.DeleteSession(sessionId);

    public void DeleteSessionData(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(DatabasePath);
        using var transaction = connection.BeginTransaction();
        EmbeddingRepository.DeleteSession(connection, transaction, sessionId);
        EvidenceRepository.DeleteSession(connection, transaction, sessionId);
        MemoryRepository.DeleteSession(connection, transaction, sessionId);
        transaction.Commit();
    }

    private static string CreateConnectionString(string databasePath) => MemoryDatabase.CreateConnectionString(databasePath);
}

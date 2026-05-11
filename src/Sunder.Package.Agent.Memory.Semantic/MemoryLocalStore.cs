using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class MemoryLocalStore
{
    public const string ActiveState = "Active";
    public const string ContestedState = "Contested";
    public const string ForgottenState = "Forgotten";
    public const string SupersededState = "Superseded";

    public MemoryLocalStore(IPackageContext packageContext)
    {
        EnsureSqliteNativeLibraryLoaded(packageContext.InstallPath);
        SQLitePCL.Batteries_V2.Init();

        DatabasePath = Path.Combine(packageContext.Storage.DataRootPath, "agent-memory.db");
        Directory.CreateDirectory(packageContext.Storage.DataRootPath);
        EnsureSchema();
        EnsureSupersessionColumnMigration();
        EnsureSearchIndexMigration();
    }

    public string DatabasePath { get; }

    public IReadOnlyList<StoredMemoryRecord> ListActiveMemories(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE SessionId = $sessionId AND State = $state
            ORDER BY IsPinned DESC, Importance DESC, UpdatedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$state", ActiveState);

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            items.Add(ReadMemory(reader));
        }

        return items;
    }

    public IReadOnlyList<StoredMemoryRecord> ListRecallableMemories(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE SessionId = $sessionId AND (State = $activeState OR State = $contestedState)
            ORDER BY IsPinned DESC, Importance DESC, UpdatedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$activeState", ActiveState);
        command.Parameters.AddWithValue("$contestedState", ContestedState);

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            items.Add(ReadMemory(reader));
        }

        return items;
    }

    public IReadOnlyList<StoredMemoryRecord> ListPriorityMemories(Guid sessionId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE SessionId = $sessionId AND State = $state
            ORDER BY IsPinned DESC,
                     CASE Category
                         WHEN 'standing-instruction' THEN 0
                         WHEN 'preference' THEN 1
                         WHEN 'project-fact' THEN 2
                         WHEN 'environment-fact' THEN 3
                         ELSE 4
                     END,
                     Importance DESC,
                     UpdatedAtUtc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$state", ActiveState);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            items.Add(ReadMemory(reader));
        }

        return items;
    }

    public StoredMemoryRecord UpsertMemory(MemoryUpsertRequest request, Guid? targetMemoryId = null)
    {
        using var connection = CreateConnection();
        connection.Open();

        var existing = targetMemoryId is Guid memoryId
            ? GetMemory(connection, memoryId)
            : FindMemory(connection, request.SessionId, request.Category, request.NormalizedContent);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            var created = new StoredMemoryRecord(
                Guid.NewGuid(),
                request.SessionId,
                request.Category,
                request.Content,
                request.EvidenceText,
                request.SourceTurnId,
                request.Importance,
                request.Confidence,
                request.IsPinned,
                ActiveState,
                null,
                now,
                now,
                null,
                0);
            using var transaction = connection.BeginTransaction();
            InsertMemory(connection, transaction, created, request.NormalizedContent, request.SourceTurnId);
            UpsertSearchIndex(connection, transaction, created);
            InsertEvidence(connection, transaction, created.MemoryId, request.SessionId, request.SourceTurnId, request.EvidenceText, now);
            transaction.Commit();
            return created;
        }

        var updated = existing with
        {
            Content = request.Content,
            EvidenceText = string.IsNullOrWhiteSpace(request.EvidenceText) ? existing.EvidenceText : request.EvidenceText,
            SourceTurnId = request.SourceTurnId ?? existing.SourceTurnId,
            Importance = Math.Max(existing.Importance, request.Importance),
            Confidence = Math.Max(existing.Confidence, request.Confidence),
            IsPinned = existing.IsPinned || request.IsPinned,
            UpdatedAtUtc = now,
        };

        using (var transaction = connection.BeginTransaction())
        {
            UpdateMemory(connection, transaction, updated, request.NormalizedContent, request.SourceTurnId);
            UpsertSearchIndex(connection, transaction, updated);
            InsertEvidence(connection, transaction, updated.MemoryId, request.SessionId, request.SourceTurnId, request.EvidenceText, now);
            transaction.Commit();
        }

        return updated;
    }

    public void RecordRecall(IReadOnlyList<Guid> memoryIds)
    {
        if (memoryIds.Count == 0)
        {
            return;
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var memoryId in memoryIds.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE SessionMemories
                SET LastAccessedAtUtc = $accessedAtUtc,
                    AccessCount = AccessCount + 1
                WHERE MemoryId = $memoryId;
                """;
            command.Parameters.AddWithValue("$accessedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<StoredMemoryRecord> ListMemories(Guid sessionId, string? searchText = null, bool includeInactive = false)
    {
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            return SearchMemories(sessionId, searchText, preferredCategories: null, includeInactive, limit: 1000)
                .Select(result => result.Memory)
                .ToArray();
        }

        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        var whereClauses = new List<string> { "SessionId = $sessionId" };
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        if (!includeInactive)
        {
            whereClauses.Add("(State = $activeState OR State = $contestedState)");
            command.Parameters.AddWithValue("$activeState", ActiveState);
            command.Parameters.AddWithValue("$contestedState", ContestedState);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            whereClauses.Add("(Content LIKE $searchText OR EvidenceText LIKE $searchText OR Category LIKE $searchText)");
            command.Parameters.AddWithValue("$searchText", $"%{searchText.Trim()}%");
        }

        command.CommandText = $"""
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY IsPinned DESC, UpdatedAtUtc DESC, CreatedAtUtc DESC;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            items.Add(ReadMemory(reader));
        }

        return items;
    }

    public IReadOnlyList<StoredMemorySearchResult> SearchMemories(
        Guid sessionId,
        string searchText,
        IReadOnlyList<string>? preferredCategories,
        bool includeInactive,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(searchText) || limit <= 0)
        {
            return [];
        }

        var matchQuery = BuildFtsMatchQuery(searchText);
        if (string.IsNullOrWhiteSpace(matchQuery))
        {
            return [];
        }

        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        var whereClauses = new List<string>
        {
            "SessionMemorySearch.SessionId = $sessionId",
            "SessionMemorySearch MATCH $matchQuery"
        };
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$matchQuery", matchQuery);
        command.Parameters.AddWithValue("$limit", limit);

        if (!includeInactive)
        {
            whereClauses.Add("(m.State = $activeState OR m.State = $contestedState)");
            command.Parameters.AddWithValue("$activeState", ActiveState);
            command.Parameters.AddWithValue("$contestedState", ContestedState);
        }

        if (preferredCategories is { Count: > 0 })
        {
            var categoryPredicates = new List<string>();
            for (var index = 0; index < preferredCategories.Count; index++)
            {
                var parameterName = "$category" + index;
                categoryPredicates.Add($"m.Category = {parameterName}");
                command.Parameters.AddWithValue(parameterName, preferredCategories[index]);
            }

            whereClauses.Add("(" + string.Join(" OR ", categoryPredicates) + ")");
        }

        command.CommandText = $"""
            SELECT m.MemoryId, m.SessionId, m.Category, m.Content, m.EvidenceText, m.SourceTurnId, m.Importance, m.Confidence, m.IsPinned, m.State, m.SupersededByMemoryId, m.CreatedAtUtc, m.UpdatedAtUtc, m.LastAccessedAtUtc, m.AccessCount,
                   bm25(SessionMemorySearch) AS SearchRank
            FROM SessionMemorySearch
            INNER JOIN SessionMemories m ON m.MemoryId = SessionMemorySearch.MemoryId
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY SearchRank ASC, m.IsPinned DESC, m.UpdatedAtUtc DESC
            LIMIT $limit;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemorySearchResult>();
        while (reader.Read())
        {
            items.Add(new StoredMemorySearchResult(ReadMemory(reader), reader.GetDouble(15)));
        }

        return items;
    }

    public StoredMemoryRecord? GetMemory(Guid memoryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE MemoryId = $memoryId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMemory(reader) : null;
    }

    public IReadOnlyList<StoredMemoryEvidenceRecord> ListEvidence(Guid memoryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EvidenceId, MemoryId, SessionId, SourceTurnId, EvidenceText, CreatedAtUtc
            FROM SessionMemoryEvidence
            WHERE MemoryId = $memoryId
            ORDER BY CreatedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryEvidenceRecord>();
        while (reader.Read())
        {
            items.Add(new StoredMemoryEvidenceRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return items;
    }

    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned, string? note = null)
    {
        using var connection = CreateConnection();
        connection.Open();

        var existing = GetMemory(connection, memoryId) ?? throw new InvalidOperationException($"Memory '{memoryId}' was not found.");
        var updated = existing with { IsPinned = isPinned, UpdatedAtUtc = DateTimeOffset.UtcNow };

        using var transaction = connection.BeginTransaction();
        UpdateMemory(connection, transaction, updated, Normalize(updated.Content), null);
        UpsertSearchIndex(connection, transaction, updated);
        InsertEvidence(connection, transaction, updated.MemoryId, updated.SessionId, null, note ?? (isPinned ? "Pinned in memory inspector." : "Unpinned in memory inspector."), updated.UpdatedAtUtc);
        transaction.Commit();
        return updated;
    }

    public StoredMemoryRecord UpdateMemory(Guid memoryId, string category, string content, string? note)
    {
        using var connection = CreateConnection();
        connection.Open();

        var existing = GetMemory(connection, memoryId) ?? throw new InvalidOperationException($"Memory '{memoryId}' was not found.");
        var normalizedContent = Normalize(content);
        EnsureUniqueMemory(connection, existing.SessionId, memoryId, category, normalizedContent);

        var updated = existing with
        {
            Category = category.Trim(),
            Content = content.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        using var transaction = connection.BeginTransaction();
        UpdateMemory(connection, transaction, updated, normalizedContent, null);
        UpsertSearchIndex(connection, transaction, updated);
        InsertEvidence(connection, transaction, updated.MemoryId, updated.SessionId, null, note ?? "Updated in memory inspector.", updated.UpdatedAtUtc);
        transaction.Commit();
        return updated;
    }

    public StoredMemoryRecord SetContested(Guid memoryId, string? note = null)
        => SetState(memoryId, ContestedState, note ?? "Marked as contested in memory inspector.");

    public MemoryCorrectionResult CreateCorrectedMemory(Guid sourceMemoryId, string category, string content, string? note)
    {
        using var connection = CreateConnection();
        connection.Open();

        var source = GetMemory(connection, sourceMemoryId) ?? throw new InvalidOperationException($"Memory '{sourceMemoryId}' was not found.");
        var trimmedCategory = category.Trim();
        var trimmedContent = content.Trim();
        var normalizedContent = Normalize(trimmedContent);
        var now = DateTimeOffset.UtcNow;

        var existingTarget = FindMemory(connection, source.SessionId, trimmedCategory, normalizedContent);
        if (existingTarget is not null && existingTarget.MemoryId == sourceMemoryId)
        {
            var updatedSource = source with
            {
                Category = trimmedCategory,
                Content = trimmedContent,
                State = ActiveState,
                SupersededByMemoryId = null,
                UpdatedAtUtc = now,
            };

            using var sameTransaction = connection.BeginTransaction();
            UpdateMemory(connection, sameTransaction, updatedSource, normalizedContent, updatedSource.SourceTurnId);
            UpsertSearchIndex(connection, sameTransaction, updatedSource);
            InsertEvidence(connection, sameTransaction, updatedSource.MemoryId, updatedSource.SessionId, null, note ?? "Corrected in memory inspector.", now);
            sameTransaction.Commit();
            return new MemoryCorrectionResult(updatedSource, updatedSource, CreatedNewMemory: false);
        }

        var target = existingTarget ?? new StoredMemoryRecord(
            Guid.NewGuid(),
            source.SessionId,
            trimmedCategory,
            trimmedContent,
            note ?? source.EvidenceText,
            source.SourceTurnId,
            Math.Max(source.Importance, 0.85f),
            Math.Max(source.Confidence, 0.9f),
            source.IsPinned,
            ActiveState,
            null,
            now,
            now,
            null,
            0);

        var updatedSourceMemory = source with
        {
            State = SupersededState,
            SupersededByMemoryId = target.MemoryId,
            UpdatedAtUtc = now,
        };

        using var transaction = connection.BeginTransaction();
        if (existingTarget is null)
        {
            InsertMemory(connection, transaction, target, normalizedContent, target.SourceTurnId);
            UpsertSearchIndex(connection, transaction, target);
            InsertEvidence(connection, transaction, target.MemoryId, target.SessionId, target.SourceTurnId, note ?? $"Created as a correction of memory '{sourceMemoryId}'.", now);
        }
        else
        {
            var mergedTarget = existingTarget with
            {
                Importance = Math.Max(existingTarget.Importance, source.Importance),
                Confidence = Math.Max(existingTarget.Confidence, source.Confidence),
                IsPinned = existingTarget.IsPinned || source.IsPinned,
                State = ActiveState,
                UpdatedAtUtc = now,
            };
            UpdateMemory(connection, transaction, mergedTarget, Normalize(mergedTarget.Content), mergedTarget.SourceTurnId);
            UpsertSearchIndex(connection, transaction, mergedTarget);
            InsertEvidence(connection, transaction, mergedTarget.MemoryId, mergedTarget.SessionId, null, note ?? $"Marked as correction target for memory '{sourceMemoryId}'.", now);
            target = mergedTarget;
        }

        UpdateMemory(connection, transaction, updatedSourceMemory, Normalize(updatedSourceMemory.Content), updatedSourceMemory.SourceTurnId);
        UpsertSearchIndex(connection, transaction, updatedSourceMemory);
        InsertEvidence(connection, transaction, updatedSourceMemory.MemoryId, updatedSourceMemory.SessionId, null, $"Superseded by memory '{target.MemoryId}'.", now);
        transaction.Commit();

        return new MemoryCorrectionResult(target, updatedSourceMemory, CreatedNewMemory: existingTarget is null);
    }

    public StoredMemoryRecord SetState(Guid memoryId, string state, string? note)
    {
        using var connection = CreateConnection();
        connection.Open();

        var existing = GetMemory(connection, memoryId) ?? throw new InvalidOperationException($"Memory '{memoryId}' was not found.");
        var updated = existing with
        {
            State = state,
            SupersededByMemoryId = string.Equals(state, SupersededState, StringComparison.OrdinalIgnoreCase)
                ? existing.SupersededByMemoryId
                : null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        using var transaction = connection.BeginTransaction();
        UpdateMemory(connection, transaction, updated, Normalize(updated.Content), null);
        UpsertSearchIndex(connection, transaction, updated);
        InsertEvidence(connection, transaction, updated.MemoryId, updated.SessionId, null, note ?? $"State changed to '{state}'.", updated.UpdatedAtUtc);
        transaction.Commit();
        return updated;
    }

    public StoredMemoryRecord? GetSupersedingMemory(Guid memoryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target.MemoryId, target.SessionId, target.Category, target.Content, target.EvidenceText, target.SourceTurnId, target.Importance, target.Confidence, target.IsPinned, target.State, target.SupersededByMemoryId, target.CreatedAtUtc, target.UpdatedAtUtc, target.LastAccessedAtUtc, target.AccessCount
            FROM SessionMemories source
            INNER JOIN SessionMemories target ON target.MemoryId = source.SupersededByMemoryId
            WHERE source.MemoryId = $memoryId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMemory(reader) : null;
    }

    public IReadOnlyList<StoredMemoryRecord> ListSupersededMemories(Guid supersedingMemoryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE SupersededByMemoryId = $memoryId
            ORDER BY UpdatedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$memoryId", supersedingMemoryId.ToString());

        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            items.Add(ReadMemory(reader));
        }

        return items;
    }

    public IReadOnlyList<StoredMemoryRecord> ListCorrectionLineage(Guid memoryId)
    {
        var related = new List<StoredMemoryRecord>();
        var visited = new HashSet<Guid> { memoryId };
        var queue = new Queue<Guid>();
        queue.Enqueue(memoryId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();

            if (GetSupersedingMemory(currentId) is { } superseding && visited.Add(superseding.MemoryId))
            {
                related.Add(superseding);
                queue.Enqueue(superseding.MemoryId);
            }

            foreach (var superseded in ListSupersededMemories(currentId))
            {
                if (!visited.Add(superseded.MemoryId))
                {
                    continue;
                }

                related.Add(superseded);
                queue.Enqueue(superseded.MemoryId);
            }
        }

        return related
            .OrderByDescending(memory => memory.UpdatedAtUtc)
            .ToArray();
    }

    public StoredMemoryEmbeddingRecord? GetEmbedding(Guid memoryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc
            FROM SessionMemoryEmbeddings
            WHERE MemoryId = $memoryId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEmbedding(reader) : null;
    }

    public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> ListEmbeddings(Guid sessionId, string providerId, string modelId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc
            FROM SessionMemoryEmbeddings
            WHERE SessionId = $sessionId AND ProviderId = $providerId AND ModelId = $modelId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        var items = new Dictionary<Guid, StoredMemoryEmbeddingRecord>();
        while (reader.Read())
        {
            var embedding = ReadEmbedding(reader);
            items[embedding.MemoryId] = embedding;
        }

        return items;
    }

    public void UpsertEmbedding(StoredMemoryEmbeddingRecord embedding)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionMemoryEmbeddings (MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($memoryId, $sessionId, $providerId, $modelId, $canonicalTextHash, $dimensions, $vectorJson, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(MemoryId) DO UPDATE SET
                SessionId = excluded.SessionId,
                ProviderId = excluded.ProviderId,
                ModelId = excluded.ModelId,
                CanonicalTextHash = excluded.CanonicalTextHash,
                Dimensions = excluded.Dimensions,
                VectorJson = excluded.VectorJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$memoryId", embedding.MemoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", embedding.SessionId.ToString());
        command.Parameters.AddWithValue("$providerId", embedding.ProviderId);
        command.Parameters.AddWithValue("$modelId", embedding.ModelId);
        command.Parameters.AddWithValue("$canonicalTextHash", embedding.CanonicalTextHash);
        command.Parameters.AddWithValue("$dimensions", embedding.Dimensions);
        command.Parameters.AddWithValue("$vectorJson", JsonSerializer.Serialize(embedding.Values));
        command.Parameters.AddWithValue("$createdAtUtc", embedding.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", embedding.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteEmbeddings(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SessionMemoryEmbeddings WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.ExecuteNonQuery();
    }

    public void DeleteSessionData(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        DeleteSessionRows(connection, transaction, "SessionMemorySearch", sessionId);
        DeleteSessionRows(connection, transaction, "SessionMemoryEmbeddings", sessionId);
        DeleteSessionRows(connection, transaction, "SessionMemoryEvidence", sessionId);
        DeleteSessionRows(connection, transaction, "SessionMemories", sessionId);

        transaction.Commit();
    }

    private static void EnsureSqliteNativeLibraryLoaded(string installPath)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "e_sqlite3.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libe_sqlite3.dylib"
                : "libe_sqlite3.so";

        foreach (var candidatePath in Directory.EnumerateFiles(installPath, fileName, SearchOption.AllDirectories))
        {
            try
            {
                NativeLibrary.Load(candidatePath);
                return;
            }
            catch
            {
                // Continue probing until a matching native binary loads successfully.
            }
        }
    }

    private SqliteConnection CreateConnection() => new(CreateConnectionString(DatabasePath));

    private static string CreateConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

    private static void DeleteSessionRows(SqliteConnection connection, SqliteTransaction transaction, string tableName, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {tableName} WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var connection = CreateConnection();
        connection.Open();

        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
            pragmaCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionMemories (
                MemoryId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                Category TEXT NOT NULL,
                Content TEXT NOT NULL,
                NormalizedContent TEXT NOT NULL,
                EvidenceText TEXT NULL,
                SourceTurnId TEXT NULL,
                Importance REAL NOT NULL,
                Confidence REAL NOT NULL,
                IsPinned INTEGER NOT NULL DEFAULT 0,
                State TEXT NOT NULL,
                SupersededByMemoryId TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastAccessedAtUtc TEXT NULL,
                AccessCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS SessionMemoryEvidence (
                EvidenceId TEXT PRIMARY KEY,
                MemoryId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                SourceTurnId TEXT NULL,
                EvidenceText TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SessionMemoryEmbeddings (
                MemoryId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                CanonicalTextHash TEXT NOT NULL,
                Dimensions INTEGER NOT NULL,
                VectorJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS SessionMemorySearch USING fts5(
                MemoryId UNINDEXED,
                SessionId UNINDEXED,
                Category,
                Content,
                EvidenceText,
                State UNINDEXED
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionMemories_SessionId_Category_NormalizedContent
                ON SessionMemories (SessionId, Category, NormalizedContent);
            CREATE INDEX IF NOT EXISTS IX_SessionMemories_SessionId_State_UpdatedAtUtc
                ON SessionMemories (SessionId, State, UpdatedAtUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryEvidence_MemoryId_CreatedAtUtc
                ON SessionMemoryEvidence (MemoryId, CreatedAtUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryEmbeddings_SessionId_ProviderId_ModelId
                ON SessionMemoryEmbeddings (SessionId, ProviderId, ModelId);
            """;
        command.ExecuteNonQuery();
    }

    private void EnsureSearchIndexMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM SessionMemorySearch;";
        var indexedCount = Convert.ToInt32(countCommand.ExecuteScalar());
        if (indexedCount > 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories;
            """;

        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            UpsertSearchIndex(connection, transaction, ReadMemory(reader));
        }

        transaction.Commit();
    }

    private void EnsureSupersessionColumnMigration()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(SessionMemories);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "SupersededByMemoryId", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE SessionMemories ADD COLUMN SupersededByMemoryId TEXT NULL;";
        alterCommand.ExecuteNonQuery();
    }

    private static StoredMemoryRecord? FindMemory(
        SqliteConnection connection,
        Guid sessionId,
        string category,
        string normalizedContent)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE SessionId = $sessionId AND Category = $category AND NormalizedContent = $normalizedContent
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$normalizedContent", normalizedContent);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMemory(reader) : null;
    }

    private static StoredMemoryRecord? GetMemory(SqliteConnection connection, Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount
            FROM SessionMemories
            WHERE MemoryId = $memoryId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMemory(reader) : null;
    }

    private static StoredMemoryRecord ReadMemory(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            Convert.ToSingle(reader.GetDouble(6)),
            Convert.ToSingle(reader.GetDouble(7)),
            reader.GetInt64(8) != 0,
            reader.GetString(9),
            reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)),
            DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            reader.GetInt32(14));

    private static void InsertMemory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMemoryRecord memory,
        string normalizedContent,
        Guid? sourceTurnId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemories (MemoryId, SessionId, Category, Content, NormalizedContent, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount)
            VALUES ($memoryId, $sessionId, $category, $content, $normalizedContent, $evidenceText, $sourceTurnId, $importance, $confidence, $isPinned, $state, $supersededByMemoryId, $createdAtUtc, $updatedAtUtc, $lastAccessedAtUtc, $accessCount);
            """;
        BindMemory(command, memory, normalizedContent, sourceTurnId);
        command.ExecuteNonQuery();
    }

    private static void UpdateMemory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMemoryRecord memory,
        string normalizedContent,
        Guid? sourceTurnId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE SessionMemories
            SET Content = $content,
                NormalizedContent = $normalizedContent,
                EvidenceText = $evidenceText,
                SourceTurnId = $sourceTurnId,
                Importance = $importance,
                Confidence = $confidence,
                IsPinned = $isPinned,
                State = $state,
                SupersededByMemoryId = $supersededByMemoryId,
                UpdatedAtUtc = $updatedAtUtc,
                LastAccessedAtUtc = $lastAccessedAtUtc,
                AccessCount = $accessCount
            WHERE MemoryId = $memoryId;
            """;
        BindMemory(command, memory, normalizedContent, sourceTurnId);
        command.ExecuteNonQuery();
    }

    private static void BindMemory(SqliteCommand command, StoredMemoryRecord memory, string normalizedContent, Guid? sourceTurnId)
    {
        command.Parameters.AddWithValue("$memoryId", memory.MemoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", memory.SessionId.ToString());
        command.Parameters.AddWithValue("$category", memory.Category);
        command.Parameters.AddWithValue("$content", memory.Content);
        command.Parameters.AddWithValue("$normalizedContent", normalizedContent);
        command.Parameters.AddWithValue("$evidenceText", (object?)memory.EvidenceText ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceTurnId", sourceTurnId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$importance", memory.Importance);
        command.Parameters.AddWithValue("$confidence", memory.Confidence);
        command.Parameters.AddWithValue("$isPinned", memory.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$state", memory.State);
        command.Parameters.AddWithValue("$supersededByMemoryId", memory.SupersededByMemoryId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", memory.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", memory.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$lastAccessedAtUtc", memory.LastAccessedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$accessCount", memory.AccessCount);
    }

    private static void InsertEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        Guid sessionId,
        Guid? sourceTurnId,
        string? evidenceText,
        DateTimeOffset createdAtUtc)
    {
        if (sourceTurnId is null && string.IsNullOrWhiteSpace(evidenceText))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemoryEvidence (EvidenceId, MemoryId, SessionId, SourceTurnId, EvidenceText, CreatedAtUtc)
            VALUES ($evidenceId, $memoryId, $sessionId, $sourceTurnId, $evidenceText, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$evidenceId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$sourceTurnId", sourceTurnId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$evidenceText", (object?)evidenceText ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertSearchIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMemoryRecord memory)
    {
        DeleteSearchIndex(connection, transaction, memory.MemoryId);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemorySearch (MemoryId, SessionId, Category, Content, EvidenceText, State)
            VALUES ($memoryId, $sessionId, $category, $content, $evidenceText, $state);
            """;
        command.Parameters.AddWithValue("$memoryId", memory.MemoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", memory.SessionId.ToString());
        command.Parameters.AddWithValue("$category", memory.Category);
        command.Parameters.AddWithValue("$content", memory.Content);
        command.Parameters.AddWithValue("$evidenceText", (object?)memory.EvidenceText ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", memory.State);
        command.ExecuteNonQuery();
    }

    private static void DeleteSearchIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SessionMemorySearch WHERE MemoryId = $memoryId;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.ExecuteNonQuery();
    }

    private static string? BuildFtsMatchQuery(string text)
    {
        var normalized = Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        var clauses = new List<string>();
        if (tokens.Length > 1)
        {
            clauses.Add("\"" + normalized.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"");
        }

        clauses.AddRange(tokens.Select(token => token + "*"));
        return string.Join(" OR ", clauses);
    }

    private static string Normalize(string text)
        => string.Join(' ', text.Trim().ToLowerInvariant().Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static void EnsureUniqueMemory(
        SqliteConnection connection,
        Guid sessionId,
        Guid currentMemoryId,
        string category,
        string normalizedContent)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM SessionMemories
            WHERE SessionId = $sessionId
              AND Category = $category
              AND NormalizedContent = $normalizedContent
              AND MemoryId <> $memoryId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$category", category.Trim());
        command.Parameters.AddWithValue("$normalizedContent", normalizedContent);
        command.Parameters.AddWithValue("$memoryId", currentMemoryId.ToString());
        var existingCount = Convert.ToInt32(command.ExecuteScalar());
        if (existingCount > 0)
        {
            throw new InvalidOperationException("Another memory with the same category and normalized content already exists in this session.");
        }
    }

    private static StoredMemoryEmbeddingRecord ReadEmbedding(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            JsonSerializer.Deserialize<IReadOnlyList<float>>(reader.GetString(6)) ?? [],
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));
}

public sealed record StoredMemoryRecord(
    Guid MemoryId,
    Guid SessionId,
    string Category,
    string Content,
    string? EvidenceText,
    Guid? SourceTurnId,
    float Importance,
    float Confidence,
    bool IsPinned,
    string State,
    Guid? SupersededByMemoryId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastAccessedAtUtc,
    int AccessCount);

public sealed record MemoryCorrectionResult(
    StoredMemoryRecord CorrectedMemory,
    StoredMemoryRecord UpdatedSourceMemory,
    bool CreatedNewMemory);

public sealed record MemoryUpsertRequest(
    Guid SessionId,
    string Category,
    string Content,
    string NormalizedContent,
    string? EvidenceText,
    Guid? SourceTurnId,
    bool IsPinned,
    float Importance,
    float Confidence);

public sealed record StoredMemoryEvidenceRecord(
    Guid EvidenceId,
    Guid MemoryId,
    Guid SessionId,
    Guid? SourceTurnId,
    string? EvidenceText,
    DateTimeOffset CreatedAtUtc);

public sealed record StoredMemoryEmbeddingRecord(
    Guid MemoryId,
    Guid SessionId,
    string ProviderId,
    string ModelId,
    string CanonicalTextHash,
    int Dimensions,
    IReadOnlyList<float> Values,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record StoredMemorySearchResult(
    StoredMemoryRecord Memory,
    double SearchRank);

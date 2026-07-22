using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static Sunder.Package.Agent.Memory.Semantic.MemoryRepositorySql;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class MemoryRepository(string databasePath, EvidenceRepository evidenceRepository)
{
    internal const int MaxCategoryChars = 64;
    internal const int MaxMemoryContentChars = 4_096;
    private const string Columns =
        "MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount, Provenance";

    private readonly string _databasePath = databasePath;
    private readonly EvidenceRepository _evidenceRepository = evidenceRepository;

    public IReadOnlyList<StoredMemoryRecord> ListActive(Guid sessionId)
        => Query($"SELECT {Columns} FROM SessionMemories WHERE SessionId = $sessionId AND State = $state ORDER BY IsPinned DESC, Importance DESC, UpdatedAtUtc DESC LIMIT $limit;",
            command =>
            {
                command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
                command.Parameters.AddWithValue("$state", MemoryLocalStore.ActiveState);
                command.Parameters.AddWithValue("$limit", MemoryLocalStore.MaxRecallableMemoriesPerSession);
            });

    public IReadOnlyList<StoredMemoryRecord> ListRecallable(Guid sessionId)
        => Query($"SELECT {Columns} FROM SessionMemories WHERE SessionId = $sessionId AND (State = $active OR State = $contested) ORDER BY IsPinned DESC, Importance DESC, UpdatedAtUtc DESC LIMIT $limit;",
            command =>
            {
                command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
                command.Parameters.AddWithValue("$active", MemoryLocalStore.ActiveState);
                command.Parameters.AddWithValue("$contested", MemoryLocalStore.ContestedState);
                command.Parameters.AddWithValue("$limit", MemoryLocalStore.MaxRecallableMemoriesPerSession);
            });

    public int CountRecallable(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SessionMemories WHERE SessionId = $sessionId AND (State = $active OR State = $contested);";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$active", MemoryLocalStore.ActiveState);
        command.Parameters.AddWithValue("$contested", MemoryLocalStore.ContestedState);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<StoredMemoryRecord> ListPriority(Guid sessionId, int limit)
        => Query($"""
            SELECT {Columns} FROM SessionMemories
            WHERE SessionId = $sessionId AND State = $state
            ORDER BY IsPinned DESC,
                CASE Category WHEN 'standing-instruction' THEN 0 WHEN 'preference' THEN 1 WHEN 'project-fact' THEN 2 WHEN 'environment-fact' THEN 3 ELSE 4 END,
                Importance DESC, UpdatedAtUtc DESC
            LIMIT $limit;
            """, command =>
            {
                command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
                command.Parameters.AddWithValue("$state", MemoryLocalStore.ActiveState);
                command.Parameters.AddWithValue("$limit", limit);
            });

    public IReadOnlyList<StoredMemoryRecord> List(Guid sessionId, string? searchText, bool includeInactive)
    {
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            return Search(sessionId, searchText, null, includeInactive, 1000).Select(item => item.Memory).ToArray();
        }

        var stateClause = includeInactive ? string.Empty : " AND (State = $active OR State = $contested)";
        return Query($"SELECT {Columns} FROM SessionMemories WHERE SessionId = $sessionId{stateClause} ORDER BY IsPinned DESC, UpdatedAtUtc DESC, CreatedAtUtc DESC;",
            command =>
            {
                command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
                if (!includeInactive)
                {
                    command.Parameters.AddWithValue("$active", MemoryLocalStore.ActiveState);
                    command.Parameters.AddWithValue("$contested", MemoryLocalStore.ContestedState);
                }
            });
    }

    public IReadOnlyList<StoredMemorySearchResult> Search(
        Guid sessionId,
        string searchText,
        IReadOnlyList<string>? preferredCategories,
        bool includeInactive,
        int limit)
    {
        var matchQuery = limit <= 0 ? null : BuildFtsMatchQuery(searchText);
        if (matchQuery is null)
        {
            return [];
        }

        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        var predicates = new List<string>
        {
            "SessionMemorySearch.SessionId = $sessionId",
            "SessionMemorySearch MATCH $matchQuery",
        };
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$matchQuery", matchQuery);
        command.Parameters.AddWithValue("$limit", limit);
        if (!includeInactive)
        {
            predicates.Add("(m.State = $active OR m.State = $contested)");
            command.Parameters.AddWithValue("$active", MemoryLocalStore.ActiveState);
            command.Parameters.AddWithValue("$contested", MemoryLocalStore.ContestedState);
        }

        if (preferredCategories is { Count: > 0 })
        {
            var categoryPredicates = new List<string>();
            for (var index = 0; index < preferredCategories.Count; index++)
            {
                categoryPredicates.Add($"m.Category = $category{index}");
                command.Parameters.AddWithValue($"$category{index}", preferredCategories[index]);
            }

            predicates.Add($"({string.Join(" OR ", categoryPredicates)})");
        }

        command.CommandText = $"""
            SELECT {AliasedColumns("m")}, bm25(SessionMemorySearch) AS SearchRank
            FROM SessionMemorySearch
            INNER JOIN SessionMemories m ON m.MemoryId = SessionMemorySearch.MemoryId
            WHERE {string.Join(" AND ", predicates)}
            ORDER BY SearchRank ASC, m.IsPinned DESC, m.UpdatedAtUtc DESC
            LIMIT $limit;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<StoredMemorySearchResult>();
        while (reader.Read())
        {
            results.Add(new StoredMemorySearchResult(Read(reader), reader.GetDouble(16)));
        }

        return results;
    }

    public StoredMemoryRecord? Get(Guid memoryId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        return Get(connection, memoryId);
    }

    public StoredMemoryRecord Upsert(MemoryUpsertRequest request, Guid? targetMemoryId)
    {
        ValidateMemoryText(request.Category, request.Content);
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        var existing = targetMemoryId is { } id
            ? Get(connection, id)
            : Find(connection, request.SessionId, request.Category, request.NormalizedContent);
        var now = DateTimeOffset.UtcNow;
        var memory = existing is null
            ? new StoredMemoryRecord(Guid.NewGuid(), request.SessionId, request.Category, request.Content, request.EvidenceText,
                request.SourceTurnId, request.Importance, request.Confidence, request.IsPinned, MemoryLocalStore.ActiveState,
                null, now, now, null, 0, request.Provenance)
            : existing with
            {
                Content = request.Content,
                EvidenceText = string.IsNullOrWhiteSpace(request.EvidenceText) ? existing.EvidenceText : request.EvidenceText,
                SourceTurnId = request.SourceTurnId ?? existing.SourceTurnId,
                Importance = Math.Max(existing.Importance, request.Importance),
                Confidence = Math.Max(existing.Confidence, request.Confidence),
                IsPinned = existing.IsPinned || request.IsPinned,
                Provenance = existing.Provenance == Sunder.Package.Agent.Contracts.Models.AgentMemoryProvenance.Unknown
                    ? request.Provenance
                    : existing.Provenance,
                UpdatedAtUtc = now,
            };

        using var transaction = connection.BeginTransaction();
        Write(connection, transaction, memory, request.NormalizedContent, insert: existing is null);
        WriteSearch(connection, transaction, memory);
        _evidenceRepository.Insert(connection, transaction, memory.MemoryId, request.SessionId, request.SourceTurnId, request.EvidenceText, now);
        transaction.Commit();
        return memory;
    }

    public void RecordRecall(IReadOnlyList<Guid> memoryIds)
    {
        if (memoryIds.Count == 0)
        {
            return;
        }

        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var memoryId in memoryIds.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE SessionMemories SET LastAccessedAtUtc = $now, AccessCount = AccessCount + 1 WHERE MemoryId = $memoryId;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned, string? note)
        => Mutate(memoryId, memory => memory with { IsPinned = isPinned, UpdatedAtUtc = DateTimeOffset.UtcNow },
            note ?? (isPinned ? "Pinned in memory inspector." : "Unpinned in memory inspector."));

    public StoredMemoryRecord Update(Guid memoryId, string category, string content, string? note)
    {
        ValidateMemoryText(category, content);
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        var existing = GetRequired(connection, memoryId);
        var normalized = Normalize(content);
        EnsureUnique(connection, existing.SessionId, memoryId, category, normalized);
        var updated = existing with { Category = category.Trim(), Content = content.Trim(), UpdatedAtUtc = DateTimeOffset.UtcNow };
        CommitMutation(connection, updated, normalized, note ?? "Updated in memory inspector.");
        return updated;
    }

    public StoredMemoryRecord SetState(Guid memoryId, string state, string? note)
        => Mutate(memoryId, memory => memory with
        {
            State = state,
            SupersededByMemoryId = string.Equals(state, MemoryLocalStore.SupersededState, StringComparison.OrdinalIgnoreCase)
                ? memory.SupersededByMemoryId
                : null,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, note ?? $"State changed to '{state}'.");

    public MemoryCorrectionResult CreateCorrection(Guid sourceMemoryId, string category, string content, string? note)
    {
        ValidateMemoryText(category, content);
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        var source = GetRequired(connection, sourceMemoryId);
        var trimmedCategory = category.Trim();
        var trimmedContent = content.Trim();
        var normalized = Normalize(trimmedContent);
        var now = DateTimeOffset.UtcNow;
        var existingTarget = Find(connection, source.SessionId, trimmedCategory, normalized);
        if (existingTarget?.MemoryId == sourceMemoryId)
        {
            var corrected = source with
            {
                Category = trimmedCategory,
                Content = trimmedContent,
                State = MemoryLocalStore.ActiveState,
                SupersededByMemoryId = null,
                UpdatedAtUtc = now,
            };
            CommitMutation(connection, corrected, normalized, note ?? "Corrected in memory inspector.");
            return new MemoryCorrectionResult(corrected, corrected, false);
        }

        var target = existingTarget ?? new StoredMemoryRecord(
            Guid.NewGuid(), source.SessionId, trimmedCategory, trimmedContent, note ?? source.EvidenceText, source.SourceTurnId,
            Math.Max(source.Importance, 0.85f), Math.Max(source.Confidence, 0.9f), source.IsPinned,
            MemoryLocalStore.ActiveState, null, now, now, null, 0, source.Provenance);
        var updatedSource = source with
        {
            State = MemoryLocalStore.SupersededState,
            SupersededByMemoryId = target.MemoryId,
            UpdatedAtUtc = now,
        };

        using var transaction = connection.BeginTransaction();
        if (existingTarget is null)
        {
            Write(connection, transaction, target, normalized, insert: true);
            WriteSearch(connection, transaction, target);
            _evidenceRepository.Insert(connection, transaction, target.MemoryId, target.SessionId, target.SourceTurnId,
                note ?? $"Created as a correction of memory '{sourceMemoryId}'.", now);
        }
        else
        {
            target = existingTarget with
            {
                Importance = Math.Max(existingTarget.Importance, source.Importance),
                Confidence = Math.Max(existingTarget.Confidence, source.Confidence),
                IsPinned = existingTarget.IsPinned || source.IsPinned,
                State = MemoryLocalStore.ActiveState,
                UpdatedAtUtc = now,
            };
            Write(connection, transaction, target, Normalize(target.Content), insert: false);
            WriteSearch(connection, transaction, target);
            _evidenceRepository.Insert(connection, transaction, target.MemoryId, target.SessionId, null,
                note ?? $"Marked as correction target for memory '{sourceMemoryId}'.", now);
        }

        Write(connection, transaction, updatedSource, Normalize(updatedSource.Content), insert: false);
        WriteSearch(connection, transaction, updatedSource);
        _evidenceRepository.Insert(connection, transaction, updatedSource.MemoryId, updatedSource.SessionId, null,
            $"Superseded by memory '{target.MemoryId}'.", now);
        transaction.Commit();
        return new MemoryCorrectionResult(target, updatedSource, existingTarget is null);
    }

    public StoredMemoryRecord? GetSuperseding(Guid memoryId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AliasedColumns("target")} FROM SessionMemories source
            INNER JOIN SessionMemories target ON target.MemoryId = source.SupersededByMemoryId
            WHERE source.MemoryId = $memoryId LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<StoredMemoryRecord> ListSuperseded(Guid memoryId)
        => Query($"SELECT {Columns} FROM SessionMemories WHERE SupersededByMemoryId = $memoryId ORDER BY UpdatedAtUtc DESC;",
            command => command.Parameters.AddWithValue("$memoryId", memoryId.ToString()));

    internal static void DeleteSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        foreach (var table in new[] { "SessionMemorySearch", "SessionMemories" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE SessionId = $sessionId;";
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.ExecuteNonQuery();
        }
    }

    private StoredMemoryRecord Mutate(Guid memoryId, Func<StoredMemoryRecord, StoredMemoryRecord> mutation, string note)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        var updated = mutation(GetRequired(connection, memoryId));
        CommitMutation(connection, updated, Normalize(updated.Content), note);
        return updated;
    }

    private void CommitMutation(SqliteConnection connection, StoredMemoryRecord memory, string normalized, string note)
    {
        using var transaction = connection.BeginTransaction();
        Write(connection, transaction, memory, normalized, insert: false);
        WriteSearch(connection, transaction, memory);
        _evidenceRepository.Insert(connection, transaction, memory.MemoryId, memory.SessionId, null, note, memory.UpdatedAtUtc);
        transaction.Commit();
    }

    private IReadOnlyList<StoredMemoryRecord> Query(string sql, Action<SqliteCommand> bind)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        using var reader = command.ExecuteReader();
        var items = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            items.Add(Read(reader));
        }

        return items;
    }

    private static StoredMemoryRecord? Get(SqliteConnection connection, Guid memoryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM SessionMemories WHERE MemoryId = $memoryId LIMIT 1;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static StoredMemoryRecord GetRequired(SqliteConnection connection, Guid memoryId)
        => Get(connection, memoryId) ?? throw new InvalidOperationException($"Memory '{memoryId}' was not found.");

    private static StoredMemoryRecord? Find(SqliteConnection connection, Guid sessionId, string category, string normalized)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM SessionMemories WHERE SessionId = $sessionId AND Category = $category AND NormalizedContent = $normalized LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$normalized", normalized);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static void EnsureUnique(SqliteConnection connection, Guid sessionId, Guid memoryId, string category, string normalized)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM SessionMemories
            WHERE SessionId = $sessionId AND Category = $category AND NormalizedContent = $normalized AND MemoryId <> $memoryId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$category", category.Trim());
        command.Parameters.AddWithValue("$normalized", normalized);
        if (Convert.ToInt32(command.ExecuteScalar()) > 0)
        {
            throw new InvalidOperationException("Another memory with the same category and normalized content already exists in this session.");
        }
    }

    private static void ValidateMemoryText(string category, string content)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > MaxCategoryChars)
        {
            throw new InvalidOperationException($"Memory category must contain between 1 and {MaxCategoryChars} characters.");
        }

        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxMemoryContentChars)
        {
            throw new InvalidOperationException($"Memory content must contain between 1 and {MaxMemoryContentChars} characters.");
        }
    }

}

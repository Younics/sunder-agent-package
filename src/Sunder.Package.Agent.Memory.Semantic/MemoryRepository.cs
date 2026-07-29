using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static Sunder.Package.Agent.Memory.Semantic.MemoryRepositorySql;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class MemoryRepository(string databasePath, EvidenceRepository evidenceRepository)
{
    internal const int MaxCategoryChars = 64;
    internal const int MaxMemoryContentChars = 4_096;
    private const string Columns =
        "MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount, Provenance, MemoryRevision, IsManual";

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
            results.Add(new StoredMemorySearchResult(Read(reader), reader.GetDouble(18)));
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
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, request.SessionId);
        var existing = targetMemoryId is { } id
            ? Get(connection, id, transaction)
            : Find(connection, request.SessionId, request.Category, request.NormalizedContent, transaction);
        if (existing is not null && existing.SessionId != request.SessionId)
        {
            throw new InvalidOperationException("A memory cannot be moved to another session.");
        }
        var now = DateTimeOffset.UtcNow;
        var memory = existing is null
            ? new StoredMemoryRecord(Guid.NewGuid(), request.SessionId, request.Category, request.Content, request.EvidenceText,
                request.SourceTurnId, request.Importance, request.Confidence, request.IsPinned, MemoryLocalStore.ActiveState,
                null, now, now, null, 0, request.Provenance)
            {
                MemoryRevision = 1,
                IsManual = true,
            }
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
                MemoryRevision = existing.MemoryRevision + 1,
                IsManual = true,
            };

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
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        foreach (var memoryId in memoryIds.Distinct())
        {
            Guid sessionId;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT SessionId FROM SessionMemories WHERE MemoryId = $memoryId;";
                select.Parameters.AddWithValue("$memoryId", memoryId.ToString());
                var value = select.ExecuteScalar() as string;
                if (value is null)
                {
                    continue;
                }
                sessionId = Guid.Parse(value);
            }
            MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, sessionId);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE SessionMemories SET LastAccessedAtUtc = $now, AccessCount = AccessCount + 1 WHERE MemoryId = $memoryId;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Memory '{memoryId}' changed or was deleted before its recall could be recorded.");
            }
        }

        transaction.Commit();
    }

    public StoredMemoryRecord SetPinned(Guid memoryId, bool isPinned, string? note)
        => Mutate(memoryId, memory => memory with
        {
            IsPinned = isPinned,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            MemoryRevision = memory.MemoryRevision + 1,
            IsManual = true,
        },
            note ?? (isPinned ? "Pinned in memory inspector." : "Unpinned in memory inspector."));

    public StoredMemoryRecord Update(Guid memoryId, string category, string content, string? note)
    {
        ValidateMemoryText(category, content);
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        var existing = GetRequired(connection, memoryId, transaction);
        MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, existing.SessionId);
        var normalized = Normalize(content);
        EnsureUnique(connection, transaction, existing.SessionId, memoryId, category, normalized);
        var updated = existing with
        {
            Category = category.Trim(),
            Content = content.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            MemoryRevision = existing.MemoryRevision + 1,
            IsManual = true,
        };
        CommitMutation(connection, transaction, updated, normalized, note ?? "Updated in memory inspector.");
        transaction.Commit();
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
            MemoryRevision = memory.MemoryRevision + 1,
            IsManual = true,
        }, note ?? $"State changed to '{state}'.");

    public MemoryCorrectionResult CreateCorrection(Guid sourceMemoryId, string category, string content, string? note)
    {
        ValidateMemoryText(category, content);
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        var source = GetRequired(connection, sourceMemoryId, transaction);
        MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, source.SessionId);
        var trimmedCategory = category.Trim();
        var trimmedContent = content.Trim();
        var normalized = Normalize(trimmedContent);
        var now = DateTimeOffset.UtcNow;
        var existingTarget = Find(connection, source.SessionId, trimmedCategory, normalized, transaction);
        if (existingTarget?.MemoryId == sourceMemoryId)
        {
            var corrected = source with
            {
                Category = trimmedCategory,
                Content = trimmedContent,
                State = MemoryLocalStore.ActiveState,
                SupersededByMemoryId = null,
                UpdatedAtUtc = now,
                MemoryRevision = source.MemoryRevision + 1,
                IsManual = true,
            };
            CommitMutation(connection, transaction, corrected, normalized, note ?? "Corrected in memory inspector.");
            transaction.Commit();
            return new MemoryCorrectionResult(corrected, corrected, false);
        }

        var target = existingTarget ?? new StoredMemoryRecord(
            Guid.NewGuid(), source.SessionId, trimmedCategory, trimmedContent, note ?? source.EvidenceText, source.SourceTurnId,
            Math.Max(source.Importance, 0.85f), Math.Max(source.Confidence, 0.9f), source.IsPinned,
            MemoryLocalStore.ActiveState, null, now, now, null, 0, source.Provenance)
        {
            MemoryRevision = 1,
            IsManual = true,
        };
        var updatedSource = source with
        {
            State = MemoryLocalStore.SupersededState,
            SupersededByMemoryId = target.MemoryId,
            UpdatedAtUtc = now,
            MemoryRevision = source.MemoryRevision + 1,
            IsManual = true,
        };

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
                MemoryRevision = existingTarget.MemoryRevision + 1,
                IsManual = true,
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
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        var existing = GetRequired(connection, memoryId, transaction);
        MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, existing.SessionId);
        var updated = mutation(existing);
        CommitMutation(connection, transaction, updated, Normalize(updated.Content), note);
        transaction.Commit();
        return updated;
    }

    private void CommitMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMemoryRecord memory,
        string normalized,
        string note)
    {
        Write(connection, transaction, memory, normalized, insert: false);
        WriteSearch(connection, transaction, memory);
        _evidenceRepository.Insert(connection, transaction, memory.MemoryId, memory.SessionId, null, note, memory.UpdatedAtUtc);
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

    private static StoredMemoryRecord? Get(
        SqliteConnection connection,
        Guid memoryId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM SessionMemories WHERE MemoryId = $memoryId LIMIT 1;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static StoredMemoryRecord GetRequired(
        SqliteConnection connection,
        Guid memoryId,
        SqliteTransaction? transaction = null)
        => Get(connection, memoryId, transaction)
           ?? throw new InvalidOperationException($"Memory '{memoryId}' was not found.");

    private static StoredMemoryRecord? Find(
        SqliteConnection connection,
        Guid sessionId,
        string category,
        string normalized,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM SessionMemories WHERE SessionId = $sessionId AND Category = $category AND NormalizedContent = $normalized LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$normalized", normalized);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static void EnsureUnique(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid memoryId,
        string category,
        string normalized)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

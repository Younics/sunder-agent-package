using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed partial class EmbeddingRepository
{
    private const string GenerationSelectColumns =
        "g.GenerationId, g.SessionId, g.ProviderId, g.ModelId, g.ConfigurationFingerprint, g.SourceFingerprint, g.MaxCanonicalTextChars, g.ExpectedMemoryCount, g.CreatedAtUtc, g.CompletedAtUtc";

    public StoredMemoryEmbeddingGenerationRecord? GetActiveGeneration(
        Guid sessionId,
        string providerId,
        string modelId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {GenerationSelectColumns}
            FROM SessionMemoryActiveEmbeddingGenerations active
            INNER JOIN SessionMemoryEmbeddingGenerations g ON g.GenerationId = active.GenerationId
            WHERE active.SessionId = $sessionId
              AND active.ProviderId = $providerId
              AND active.ModelId = $modelId
            LIMIT 1;
            """;
        BindModel(command, sessionId, providerId, modelId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGeneration(reader) : null;
    }

    public IReadOnlyList<StoredMemoryEmbeddingGenerationRecord> ListActiveGenerations(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {GenerationSelectColumns}
            FROM SessionMemoryActiveEmbeddingGenerations active
            INNER JOIN SessionMemoryEmbeddingGenerations g ON g.GenerationId = active.GenerationId
            WHERE active.SessionId = $sessionId
            ORDER BY g.ProviderId, g.ModelId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        var generations = new List<StoredMemoryEmbeddingGenerationRecord>();
        while (reader.Read())
        {
            generations.Add(ReadGeneration(reader));
        }

        return generations;
    }

    public bool HasRetractions(Guid sessionId, IReadOnlySet<Guid> retainedMemoryIds)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MemoryId FROM SessionMemoryEmbeddings WHERE SessionId = $sessionId;";
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!retainedMemoryIds.Contains(Guid.Parse(reader.GetString(0))))
                {
                    return true;
                }
            }
        }

        using var staging = connection.CreateCommand();
        staging.CommandText = """
            SELECT 1
            FROM SessionMemoryEmbeddingGenerations
            WHERE SessionId = $sessionId
              AND State = 'Staging'
              AND ExpectedMemoryCount <> $retainedCount
            LIMIT 1;
            """;
        staging.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        staging.Parameters.AddWithValue("$retainedCount", retainedMemoryIds.Count);
        return staging.ExecuteScalar() is not null;
    }

    public int PruneSessionGenerations(Guid sessionId, IReadOnlySet<Guid> retainedMemoryIds)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        if (MemoryDatabase.IsSessionDeleted(connection, transaction, sessionId))
        {
            var deleted = CountSessionEmbeddings(connection, transaction, sessionId);
            DeleteSession(connection, transaction, sessionId);
            transaction.Commit();
            if (deleted > 0)
            {
                MemoryDatabase.TruncateWal(connection);
            }
            return deleted;
        }

        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        if (retainedMemoryIds.Count == 0)
        {
            prune.CommandText = "DELETE FROM SessionMemoryEmbeddings WHERE SessionId = $sessionId;";
        }
        else
        {
            var retainedParameters = new List<string>(retainedMemoryIds.Count);
            var index = 0;
            foreach (var memoryId in retainedMemoryIds)
            {
                var name = $"$memoryId{index++}";
                retainedParameters.Add(name);
                prune.Parameters.AddWithValue(name, memoryId.ToString());
            }
            prune.CommandText = $"""
                DELETE FROM SessionMemoryEmbeddings
                WHERE SessionId = $sessionId
                  AND MemoryId NOT IN ({string.Join(", ", retainedParameters)});
                """;
        }
        var removed = prune.ExecuteNonQuery();

        var staleRemoved = 0;
        using (var staleStaging = connection.CreateCommand())
        {
            staleStaging.Transaction = transaction;
            staleStaging.CommandText = """
                DELETE FROM SessionMemoryEmbeddings
                WHERE GenerationId IN (
                    SELECT GenerationId
                    FROM SessionMemoryEmbeddingGenerations
                    WHERE SessionId = $sessionId
                      AND State = 'Staging'
                      AND ExpectedMemoryCount <> $retainedCount
                );
                DELETE FROM SessionMemoryEmbeddingGenerations
                WHERE SessionId = $sessionId
                  AND State = 'Staging'
                  AND ExpectedMemoryCount <> $retainedCount;
                """;
            staleStaging.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            staleStaging.Parameters.AddWithValue("$retainedCount", retainedMemoryIds.Count);
            staleRemoved = staleStaging.ExecuteNonQuery();
        }

        using (var counts = connection.CreateCommand())
        {
            counts.Transaction = transaction;
            counts.CommandText = """
                UPDATE SessionMemoryEmbeddingGenerations
                SET ExpectedMemoryCount = (
                    SELECT COUNT(*)
                    FROM SessionMemoryEmbeddings embedding
                    WHERE embedding.GenerationId = SessionMemoryEmbeddingGenerations.GenerationId)
                WHERE SessionId = $sessionId AND State = 'Complete';
                """;
            counts.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            counts.ExecuteNonQuery();
        }
        transaction.Commit();
        if (removed > 0 || staleRemoved > 0)
        {
            MemoryDatabase.TruncateWal(connection);
        }
        return removed;
    }

    private static int CountSessionEmbeddings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM SessionMemoryEmbeddings WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static StoredMemoryEmbeddingGenerationRecord ReadGeneration(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)));

    private sealed record EmbeddingGeneration(
        Guid SessionId,
        string ProviderId,
        string ModelId,
        int ExpectedMemoryCount,
        string? ConfigurationFingerprint,
        string? SourceFingerprint,
        int? MaxCanonicalTextChars);
}

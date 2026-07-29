using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed partial class EmbeddingRepository(string databasePath)
{
    private const string SelectColumns =
        "e.MemoryId, e.SessionId, e.ProviderId, e.ModelId, e.CanonicalTextHash, e.Dimensions, e.VectorJson, e.CreatedAtUtc, e.UpdatedAtUtc, e.MemoryRevision";

    private readonly string _databasePath = databasePath;

    public StoredMemoryEmbeddingRecord? Get(Guid memoryId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM SessionMemoryEmbeddings e
            INNER JOIN SessionMemoryActiveEmbeddingGenerations active ON active.GenerationId = e.GenerationId
            INNER JOIN SessionMemoryEmbeddingGenerations generation ON generation.GenerationId = e.GenerationId
            WHERE e.MemoryId = $memoryId
            ORDER BY generation.CompletedAtUtc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public StoredMemoryEmbeddingRecord? Get(Guid memoryId, string providerId, string modelId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM SessionMemoryEmbeddings e
            INNER JOIN SessionMemoryActiveEmbeddingGenerations active ON active.GenerationId = e.GenerationId
            WHERE e.MemoryId = $memoryId AND e.ProviderId = $providerId AND e.ModelId = $modelId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$providerId", NormalizeProviderId(providerId));
        command.Parameters.AddWithValue("$modelId", modelId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> List(Guid sessionId, string providerId, string modelId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM SessionMemoryEmbeddings e
            INNER JOIN SessionMemoryActiveEmbeddingGenerations active ON active.GenerationId = e.GenerationId
            WHERE e.SessionId = $sessionId AND e.ProviderId = $providerId AND e.ModelId = $modelId;
            """;
        BindModel(command, sessionId, providerId, modelId);
        using var reader = command.ExecuteReader();
        var items = new Dictionary<Guid, StoredMemoryEmbeddingRecord>();
        while (reader.Read())
        {
            var embedding = Read(reader);
            items[embedding.MemoryId] = embedding;
        }

        return items;
    }

    public IReadOnlyDictionary<Guid, StoredMemoryEmbeddingRecord> ListGeneration(string generationId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM SessionMemoryEmbeddings e
            WHERE e.GenerationId = $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        var items = new Dictionary<Guid, StoredMemoryEmbeddingRecord>();
        while (reader.Read())
        {
            var embedding = Read(reader);
            items[embedding.MemoryId] = embedding;
        }

        return items;
    }

    public void Upsert(StoredMemoryEmbeddingRecord embedding)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, embedding.SessionId);
        var generationId = GetActiveGenerationId(
                               connection,
                               transaction,
                               embedding.SessionId,
                               embedding.ProviderId,
                               embedding.ModelId)
                           ?? CreateCompleteGeneration(
                               connection,
                               transaction,
                               embedding.SessionId,
                               embedding.ProviderId,
                               embedding.ModelId,
                               configurationFingerprint: null);
        WriteEmbedding(connection, transaction, generationId, embedding);
        UpdateExpectedCount(connection, transaction, generationId);
        transaction.Commit();
    }

    public bool TryUpsertFenced(
        StoredMemoryEmbeddingRecord embedding,
        long expectedMemoryRevision,
        string expectedCanonicalTextHash,
        int maxCanonicalTextChars,
        string configurationFingerprint)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        if (!CanPersistEmbedding(
                connection,
                transaction,
                embedding,
                expectedMemoryRevision,
                expectedCanonicalTextHash,
                maxCanonicalTextChars))
        {
            transaction.Rollback();
            return false;
        }

        var generationId = GetActiveGenerationId(
            connection,
            transaction,
            embedding.SessionId,
            embedding.ProviderId,
            embedding.ModelId,
            configurationFingerprint);
        if (generationId is null)
        {
            transaction.Rollback();
            return false;
        }
        WriteEmbedding(connection, transaction, generationId, embedding with { MemoryRevision = expectedMemoryRevision });
        UpdateExpectedCount(connection, transaction, generationId);
        transaction.Commit();
        return true;
    }

    public string GetOrBeginGeneration(
        Guid sessionId,
        string providerId,
        string modelId,
        string configurationFingerprint,
        string sourceFingerprint,
        int maxCanonicalTextChars,
        int expectedMemoryCount)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        MemoryDatabase.ThrowIfSessionDeleted(connection, transaction, sessionId);
        string? generationId;
        using (var resumable = connection.CreateCommand())
        {
            resumable.Transaction = transaction;
            resumable.CommandText = """
                SELECT GenerationId
                FROM SessionMemoryEmbeddingGenerations
                WHERE SessionId = $sessionId
                  AND ProviderId = $providerId
                  AND ModelId = $modelId
                  AND State = 'Staging'
                  AND ConfigurationFingerprint = $configurationFingerprint
                  AND SourceFingerprint = $sourceFingerprint
                  AND MaxCanonicalTextChars = $maxCanonicalTextChars
                  AND ExpectedMemoryCount = $expectedMemoryCount
                ORDER BY CreatedAtUtc DESC, GenerationId DESC
                LIMIT 1;
                """;
            BindModel(resumable, sessionId, providerId, modelId);
            resumable.Parameters.AddWithValue("$configurationFingerprint", configurationFingerprint);
            resumable.Parameters.AddWithValue("$sourceFingerprint", sourceFingerprint);
            resumable.Parameters.AddWithValue("$maxCanonicalTextChars", maxCanonicalTextChars);
            resumable.Parameters.AddWithValue("$expectedMemoryCount", expectedMemoryCount);
            generationId = resumable.ExecuteScalar() as string;
        }

        DeleteOtherStagingGenerations(
            connection,
            transaction,
            sessionId,
            providerId,
            modelId,
            generationId);
        if (generationId is null)
        {
            generationId = Guid.NewGuid().ToString("N");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SessionMemoryEmbeddingGenerations
                    (GenerationId, SessionId, ProviderId, ModelId, State, ExpectedMemoryCount, CreatedAtUtc, CompletedAtUtc,
                     ConfigurationFingerprint, SourceFingerprint, MaxCanonicalTextChars)
                VALUES ($generationId, $sessionId, $providerId, $modelId, 'Staging', $expectedMemoryCount, $createdAtUtc, NULL,
                        $configurationFingerprint, $sourceFingerprint, $maxCanonicalTextChars);
                """;
            command.Parameters.AddWithValue("$generationId", generationId);
            BindModel(command, sessionId, providerId, modelId);
            command.Parameters.AddWithValue("$expectedMemoryCount", expectedMemoryCount);
            command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$configurationFingerprint", configurationFingerprint);
            command.Parameters.AddWithValue("$sourceFingerprint", sourceFingerprint);
            command.Parameters.AddWithValue("$maxCanonicalTextChars", maxCanonicalTextChars);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Embedding generation '{generationId}' could not be created.");
            }
        }
        transaction.Commit();
        return generationId;
    }

    public int PruneGeneration(string generationId, IReadOnlySet<Guid> retainedMemoryIds)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$generationId", generationId);
        if (retainedMemoryIds.Count == 0)
        {
            command.CommandText = "DELETE FROM SessionMemoryEmbeddings WHERE GenerationId = $generationId;";
        }
        else
        {
            var parameterNames = new List<string>(retainedMemoryIds.Count);
            var index = 0;
            foreach (var memoryId in retainedMemoryIds)
            {
                var parameterName = $"$memoryId{index++}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, memoryId.ToString());
            }
            command.CommandText = $"""
                DELETE FROM SessionMemoryEmbeddings
                WHERE GenerationId = $generationId
                  AND MemoryId NOT IN ({string.Join(", ", parameterNames)});
                """;
        }

        var removed = command.ExecuteNonQuery();
        UpdateExpectedCount(connection, transaction, generationId);
        transaction.Commit();
        if (removed > 0)
        {
            MemoryDatabase.TruncateWal(connection);
        }
        return removed;
    }

    private static void DeleteOtherStagingGenerations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string providerId,
        string modelId,
        string? retainedGenerationId)
    {
        using (var embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText = """
                DELETE FROM SessionMemoryEmbeddings
                WHERE GenerationId IN (
                    SELECT GenerationId
                    FROM SessionMemoryEmbeddingGenerations
                    WHERE SessionId = $sessionId
                      AND ProviderId = $providerId
                      AND ModelId = $modelId
                      AND State = 'Staging'
                      AND ($retainedGenerationId IS NULL OR GenerationId <> $retainedGenerationId)
                );
                """;
            BindModel(embeddings, sessionId, providerId, modelId);
            embeddings.Parameters.AddWithValue("$retainedGenerationId", (object?)retainedGenerationId ?? DBNull.Value);
            embeddings.ExecuteNonQuery();
        }

        using var generations = connection.CreateCommand();
        generations.Transaction = transaction;
        generations.CommandText = """
            DELETE FROM SessionMemoryEmbeddingGenerations
            WHERE SessionId = $sessionId
              AND ProviderId = $providerId
              AND ModelId = $modelId
              AND State = 'Staging'
              AND ($retainedGenerationId IS NULL OR GenerationId <> $retainedGenerationId);
            """;
        BindModel(generations, sessionId, providerId, modelId);
        generations.Parameters.AddWithValue("$retainedGenerationId", (object?)retainedGenerationId ?? DBNull.Value);
        generations.ExecuteNonQuery();
    }

    public bool TryStageFenced(
        string generationId,
        StoredMemoryEmbeddingRecord embedding,
        long expectedMemoryRevision,
        string expectedCanonicalTextHash,
        int maxCanonicalTextChars)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        EnsureStagingGeneration(connection, transaction, generationId, embedding);
        if (!CanPersistEmbedding(
                connection,
                transaction,
                embedding,
                expectedMemoryRevision,
                expectedCanonicalTextHash,
                maxCanonicalTextChars))
        {
            transaction.Rollback();
            return false;
        }

        WriteEmbedding(connection, transaction, generationId, embedding with { MemoryRevision = expectedMemoryRevision });
        transaction.Commit();
        return true;
    }

    public void CompleteGeneration(string generationId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        var generation = GetGeneration(connection, transaction, generationId)
                         ?? throw new InvalidOperationException($"Embedding generation '{generationId}' was not found.");
        var actualCount = CountEmbeddings(connection, transaction, generationId);
        if (actualCount != generation.ExpectedMemoryCount)
        {
            throw new InvalidOperationException(
                $"Embedding generation '{generationId}' is incomplete: expected {generation.ExpectedMemoryCount}, staged {actualCount}.");
        }

        if (HasDeletedOrStaleGenerationSource(connection, transaction, generationId, generation.SessionId))
        {
            throw new InvalidOperationException(
                $"Embedding generation '{generationId}' is stale because its session or source memory changed.");
        }

        using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE SessionMemoryEmbeddingGenerations
                SET State = 'Complete', CompletedAtUtc = $completedAtUtc
                WHERE GenerationId = $generationId AND State = 'Staging';
                """;
            complete.Parameters.AddWithValue("$completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            complete.Parameters.AddWithValue("$generationId", generationId);
            if (complete.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Embedding generation '{generationId}' is not staging.");
            }
        }

        using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                INSERT INTO SessionMemoryActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
                VALUES ($sessionId, $providerId, $modelId, $generationId)
                ON CONFLICT(SessionId, ProviderId, ModelId) DO UPDATE SET GenerationId = excluded.GenerationId;
                """;
            activate.Parameters.AddWithValue("$generationId", generationId);
            BindModel(activate, generation.SessionId, generation.ProviderId, generation.ModelId);
            if (activate.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Embedding generation '{generationId}' could not be activated.");
            }
        }

        using (var deleteEmbeddings = connection.CreateCommand())
        {
            deleteEmbeddings.Transaction = transaction;
            deleteEmbeddings.CommandText = """
                DELETE FROM SessionMemoryEmbeddings
                WHERE GenerationId IN (
                    SELECT GenerationId FROM SessionMemoryEmbeddingGenerations
                    WHERE SessionId = $sessionId AND ProviderId = $providerId AND ModelId = $modelId AND GenerationId <> $generationId
                );
                """;
            deleteEmbeddings.Parameters.AddWithValue("$generationId", generationId);
            BindModel(deleteEmbeddings, generation.SessionId, generation.ProviderId, generation.ModelId);
            deleteEmbeddings.ExecuteNonQuery();
        }

        using (var deleteGenerations = connection.CreateCommand())
        {
            deleteGenerations.Transaction = transaction;
            deleteGenerations.CommandText = """
                DELETE FROM SessionMemoryEmbeddingGenerations
                WHERE SessionId = $sessionId AND ProviderId = $providerId AND ModelId = $modelId AND GenerationId <> $generationId;
                """;
            deleteGenerations.Parameters.AddWithValue("$generationId", generationId);
            BindModel(deleteGenerations, generation.SessionId, generation.ProviderId, generation.ModelId);
            deleteGenerations.ExecuteNonQuery();
        }

        transaction.Commit();
        MemoryDatabase.TruncateWal(connection);
    }

    public void AbortGeneration(string generationId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        using (var embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText = "DELETE FROM SessionMemoryEmbeddings WHERE GenerationId = $generationId;";
            embeddings.Parameters.AddWithValue("$generationId", generationId);
            embeddings.ExecuteNonQuery();
        }

        using (var generation = connection.CreateCommand())
        {
            generation.Transaction = transaction;
            generation.CommandText = "DELETE FROM SessionMemoryEmbeddingGenerations WHERE GenerationId = $generationId AND State = 'Staging';";
            generation.Parameters.AddWithValue("$generationId", generationId);
            generation.ExecuteNonQuery();
        }

        transaction.Commit();
        MemoryDatabase.TruncateWal(connection);
    }

    public void DeleteSession(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        DeleteSession(connection, transaction, sessionId);
        transaction.Commit();
        MemoryDatabase.TruncateWal(connection);
    }

    internal static void DeleteSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        foreach (var table in new[]
                 {
                     "SessionMemoryEmbeddings",
                     "SessionMemoryActiveEmbeddingGenerations",
                     "SessionMemoryEmbeddingGenerations",
                 })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE SessionId = $sessionId;";
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.ExecuteNonQuery();
        }
    }

    private static string? GetActiveGenerationId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string providerId,
        string modelId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT GenerationId FROM SessionMemoryActiveEmbeddingGenerations
            WHERE SessionId = $sessionId AND ProviderId = $providerId AND ModelId = $modelId;
            """;
        BindModel(command, sessionId, providerId, modelId);
        return command.ExecuteScalar() as string;
    }

    private static string? GetActiveGenerationId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string providerId,
        string modelId,
        string configurationFingerprint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT active.GenerationId
            FROM SessionMemoryActiveEmbeddingGenerations active
            INNER JOIN SessionMemoryEmbeddingGenerations generation ON generation.GenerationId = active.GenerationId
            WHERE active.SessionId = $sessionId
              AND active.ProviderId = $providerId
              AND active.ModelId = $modelId
              AND generation.ConfigurationFingerprint = $configurationFingerprint;
            """;
        BindModel(command, sessionId, providerId, modelId);
        command.Parameters.AddWithValue("$configurationFingerprint", configurationFingerprint);
        return command.ExecuteScalar() as string;
    }

    private static EmbeddingGeneration? GetGeneration(SqliteConnection connection, SqliteTransaction transaction, string generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SessionId, ProviderId, ModelId, ExpectedMemoryCount, ConfigurationFingerprint,
                   SourceFingerprint, MaxCanonicalTextChars
            FROM SessionMemoryEmbeddingGenerations
            WHERE GenerationId = $generationId AND State = 'Staging';
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EmbeddingGeneration(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6))
            : null;
    }

    private static void EnsureStagingGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        StoredMemoryEmbeddingRecord embedding)
    {
        var generation = GetGeneration(connection, transaction, generationId)
                         ?? throw new InvalidOperationException($"Embedding generation '{generationId}' is not staging.");
        if (generation.SessionId != embedding.SessionId
            || !string.Equals(generation.ProviderId, embedding.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(generation.ModelId, embedding.ModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The embedding does not belong to the staging generation.");
        }
    }

    private static int CountEmbeddings(SqliteConnection connection, SqliteTransaction transaction, string generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM SessionMemoryEmbeddings WHERE GenerationId = $generationId;";
        command.Parameters.AddWithValue("$generationId", generationId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void UpdateExpectedCount(SqliteConnection connection, SqliteTransaction transaction, string generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE SessionMemoryEmbeddingGenerations
            SET ExpectedMemoryCount = (SELECT COUNT(*) FROM SessionMemoryEmbeddings WHERE GenerationId = $generationId)
            WHERE GenerationId = $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Embedding generation '{generationId}' changed or was deleted before its count could be updated.");
        }
    }

    private static void WriteEmbedding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        StoredMemoryEmbeddingRecord embedding)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemoryEmbeddings
                (GenerationId, MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc, MemoryRevision)
            VALUES ($generationId, $memoryId, $sessionId, $providerId, $modelId, $canonicalTextHash, $dimensions, $vectorJson, $createdAtUtc, $updatedAtUtc, $memoryRevision)
            ON CONFLICT(GenerationId, MemoryId) DO UPDATE SET
                CanonicalTextHash = excluded.CanonicalTextHash,
                Dimensions = excluded.Dimensions,
                VectorJson = excluded.VectorJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                MemoryRevision = excluded.MemoryRevision;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$memoryId", embedding.MemoryId.ToString());
        BindModel(command, embedding.SessionId, embedding.ProviderId, embedding.ModelId);
        command.Parameters.AddWithValue("$canonicalTextHash", embedding.CanonicalTextHash);
        command.Parameters.AddWithValue("$dimensions", embedding.Dimensions);
        command.Parameters.AddWithValue("$vectorJson", JsonSerializer.Serialize(embedding.Values));
        command.Parameters.AddWithValue("$createdAtUtc", embedding.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", embedding.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$memoryRevision", embedding.MemoryRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Embedding '{embedding.MemoryId}' could not be persisted in generation '{generationId}'.");
        }
    }

    private static bool CanPersistEmbedding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMemoryEmbeddingRecord embedding,
        long expectedMemoryRevision,
        string expectedCanonicalTextHash,
        int maxCanonicalTextChars)
    {
        using (var tombstone = connection.CreateCommand())
        {
            tombstone.Transaction = transaction;
            tombstone.CommandText = "SELECT 1 FROM SessionMemoryDeletionTombstones WHERE SessionId = $sessionId LIMIT 1;";
            tombstone.Parameters.AddWithValue("$sessionId", embedding.SessionId.ToString());
            if (tombstone.ExecuteScalar() is not null)
            {
                return false;
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Category, Content, EvidenceText, State, MemoryRevision
            FROM SessionMemories
            WHERE MemoryId = $memoryId AND SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$memoryId", embedding.MemoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", embedding.SessionId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(4) != expectedMemoryRevision)
        {
            return false;
        }

        var canonicalText = BuildCanonicalText(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            maxCanonicalTextChars);
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
        return string.Equals(actualHash, expectedCanonicalTextHash, StringComparison.Ordinal)
               && string.Equals(actualHash, embedding.CanonicalTextHash, StringComparison.Ordinal);
    }

    private static bool HasDeletedOrStaleGenerationSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        Guid sessionId)
    {
        var generation = GetGeneration(connection, transaction, generationId);
        if (generation?.SourceFingerprint is null || generation.MaxCanonicalTextChars is null)
        {
            return true;
        }

        using (var tombstone = connection.CreateCommand())
        {
            tombstone.Transaction = transaction;
            tombstone.CommandText = "SELECT 1 FROM SessionMemoryDeletionTombstones WHERE SessionId = $sessionId LIMIT 1;";
            tombstone.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            if (tombstone.ExecuteScalar() is not null)
            {
                return true;
            }
        }

        using (var sources = connection.CreateCommand())
        {
            sources.Transaction = transaction;
            sources.CommandText = """
                SELECT embedding.SessionId, embedding.CanonicalTextHash, embedding.MemoryRevision,
                       memory.Category, memory.Content, memory.EvidenceText, memory.State, memory.MemoryRevision
                FROM SessionMemoryEmbeddings embedding
                LEFT JOIN SessionMemories memory ON memory.MemoryId = embedding.MemoryId
                WHERE embedding.GenerationId = $generationId;
                """;
            sources.Parameters.AddWithValue("$generationId", generationId);
            using var reader = sources.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(3)
                    || !string.Equals(reader.GetString(0), sessionId.ToString(), StringComparison.Ordinal)
                    || reader.GetInt64(2) != reader.GetInt64(7)
                    || reader.GetString(6) is not (MemoryLocalStore.ActiveState or MemoryLocalStore.ContestedState))
                {
                    return true;
                }

                var canonicalText = BuildCanonicalText(
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    generation.MaxCanonicalTextChars.Value);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
                if (!string.Equals(hash, reader.GetString(1), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        using var missingSource = connection.CreateCommand();
        missingSource.Transaction = transaction;
        missingSource.CommandText = """
            SELECT 1
            FROM SessionMemories memory
            WHERE memory.SessionId = $sessionId
              AND (memory.State = $active OR memory.State = $contested)
              AND NOT EXISTS (
                  SELECT 1
                  FROM SessionMemoryEmbeddings embedding
                  WHERE embedding.GenerationId = $generationId
                    AND embedding.MemoryId = memory.MemoryId)
            LIMIT 1;
            """;
        missingSource.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        missingSource.Parameters.AddWithValue("$generationId", generationId);
        missingSource.Parameters.AddWithValue("$active", MemoryLocalStore.ActiveState);
        missingSource.Parameters.AddWithValue("$contested", MemoryLocalStore.ContestedState);
        return missingSource.ExecuteScalar() is not null;
    }

    private static string BuildCanonicalText(
        string category,
        string content,
        string? evidenceText,
        string state,
        int maxLength)
    {
        var builder = new StringBuilder();
        builder.Append("Category: ").AppendLine(category);
        builder.Append("Content: ").AppendLine(content);
        if (!string.IsNullOrWhiteSpace(evidenceText))
        {
            builder.Append("Evidence: ").AppendLine(evidenceText.Trim());
        }
        builder.Append("Trust: ").Append(state);
        var canonicalText = builder.ToString().Trim();
        return canonicalText.Length <= maxLength
            ? canonicalText
            : canonicalText[..maxLength].TrimEnd();
    }

    private static void BindModel(SqliteCommand command, Guid sessionId, string providerId, string modelId)
    {
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$providerId", NormalizeProviderId(providerId));
        command.Parameters.AddWithValue("$modelId", modelId);
    }

    private static string NormalizeProviderId(string providerId) => providerId.Trim().ToLowerInvariant();

    private static StoredMemoryEmbeddingRecord Read(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            JsonSerializer.Deserialize<IReadOnlyList<float>>(reader.GetString(6)) ?? [],
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)))
        {
            MemoryRevision = reader.GetInt64(9),
        };

}

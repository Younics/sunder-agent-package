using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class EmbeddingRepository(string databasePath)
{
    private const string SelectColumns =
        "e.MemoryId, e.SessionId, e.ProviderId, e.ModelId, e.CanonicalTextHash, e.Dimensions, e.VectorJson, e.CreatedAtUtc, e.UpdatedAtUtc";

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
        command.Parameters.AddWithValue("$providerId", providerId);
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

    public void Upsert(StoredMemoryEmbeddingRecord embedding)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
        var generationId = GetActiveGenerationId(connection, transaction, embedding.SessionId, embedding.ProviderId, embedding.ModelId)
                           ?? CreateCompleteGeneration(connection, transaction, embedding.SessionId, embedding.ProviderId, embedding.ModelId);
        WriteEmbedding(connection, transaction, generationId, embedding);
        UpdateExpectedCount(connection, transaction, generationId);
        transaction.Commit();
    }

    public string BeginGeneration(Guid sessionId, string providerId, string modelId, int expectedMemoryCount)
    {
        var generationId = Guid.NewGuid().ToString("N");
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionMemoryEmbeddingGenerations
                (GenerationId, SessionId, ProviderId, ModelId, State, ExpectedMemoryCount, CreatedAtUtc, CompletedAtUtc)
            VALUES ($generationId, $sessionId, $providerId, $modelId, 'Staging', $expectedMemoryCount, $createdAtUtc, NULL);
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        BindModel(command, sessionId, providerId, modelId);
        command.Parameters.AddWithValue("$expectedMemoryCount", expectedMemoryCount);
        command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return generationId;
    }

    public void Stage(string generationId, StoredMemoryEmbeddingRecord embedding)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
        EnsureStagingGeneration(connection, transaction, generationId, embedding);
        WriteEmbedding(connection, transaction, generationId, embedding);
        transaction.Commit();
    }

    public void CompleteGeneration(string generationId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
        var generation = GetGeneration(connection, transaction, generationId)
                         ?? throw new InvalidOperationException($"Embedding generation '{generationId}' was not found.");
        var actualCount = CountEmbeddings(connection, transaction, generationId);
        if (actualCount != generation.ExpectedMemoryCount)
        {
            throw new InvalidOperationException(
                $"Embedding generation '{generationId}' is incomplete: expected {generation.ExpectedMemoryCount}, staged {actualCount}.");
        }

        using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE SessionMemoryEmbeddingGenerations
                SET State = 'Complete', CompletedAtUtc = $completedAtUtc
                WHERE GenerationId = $generationId AND State = 'Staging';
                INSERT INTO SessionMemoryActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
                VALUES ($sessionId, $providerId, $modelId, $generationId)
                ON CONFLICT(SessionId, ProviderId, ModelId) DO UPDATE SET GenerationId = excluded.GenerationId;
                """;
            complete.Parameters.AddWithValue("$completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            complete.Parameters.AddWithValue("$generationId", generationId);
            BindModel(complete, generation.SessionId, generation.ProviderId, generation.ModelId);
            if (complete.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException($"Embedding generation '{generationId}' is not staging.");
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
    }

    public void AbortGeneration(string generationId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
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
    }

    public int CleanupStagingGenerations()
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
        using (var embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText = """
                DELETE FROM SessionMemoryEmbeddings
                WHERE GenerationId IN (
                    SELECT GenerationId FROM SessionMemoryEmbeddingGenerations WHERE State = 'Staging'
                );
                """;
            embeddings.ExecuteNonQuery();
        }

        int removed;
        using (var generations = connection.CreateCommand())
        {
            generations.Transaction = transaction;
            generations.CommandText = "DELETE FROM SessionMemoryEmbeddingGenerations WHERE State = 'Staging';";
            removed = generations.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed;
    }

    public void DeleteSession(Guid sessionId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();
        DeleteSession(connection, transaction, sessionId);
        transaction.Commit();
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

    private static string CreateCompleteGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string providerId,
        string modelId)
    {
        var generationId = Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SessionMemoryEmbeddingGenerations
                (GenerationId, SessionId, ProviderId, ModelId, State, ExpectedMemoryCount, CreatedAtUtc, CompletedAtUtc)
            VALUES ($generationId, $sessionId, $providerId, $modelId, 'Complete', 0, $now, $now);
            INSERT INTO SessionMemoryActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
            VALUES ($sessionId, $providerId, $modelId, $generationId)
            ON CONFLICT(SessionId, ProviderId, ModelId) DO UPDATE SET GenerationId = excluded.GenerationId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        BindModel(command, sessionId, providerId, modelId);
        command.ExecuteNonQuery();
        return generationId;
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

    private static EmbeddingGeneration? GetGeneration(SqliteConnection connection, SqliteTransaction transaction, string generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SessionId, ProviderId, ModelId, ExpectedMemoryCount
            FROM SessionMemoryEmbeddingGenerations
            WHERE GenerationId = $generationId AND State = 'Staging';
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EmbeddingGeneration(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3))
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
            || !string.Equals(generation.ModelId, embedding.ModelId, StringComparison.OrdinalIgnoreCase))
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
        command.ExecuteNonQuery();
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
                (GenerationId, MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($generationId, $memoryId, $sessionId, $providerId, $modelId, $canonicalTextHash, $dimensions, $vectorJson, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(GenerationId, MemoryId) DO UPDATE SET
                CanonicalTextHash = excluded.CanonicalTextHash,
                Dimensions = excluded.Dimensions,
                VectorJson = excluded.VectorJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$memoryId", embedding.MemoryId.ToString());
        BindModel(command, embedding.SessionId, embedding.ProviderId, embedding.ModelId);
        command.Parameters.AddWithValue("$canonicalTextHash", embedding.CanonicalTextHash);
        command.Parameters.AddWithValue("$dimensions", embedding.Dimensions);
        command.Parameters.AddWithValue("$vectorJson", JsonSerializer.Serialize(embedding.Values));
        command.Parameters.AddWithValue("$createdAtUtc", embedding.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", embedding.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void BindModel(SqliteCommand command, Guid sessionId, string providerId, string modelId)
    {
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$modelId", modelId);
    }

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
            DateTimeOffset.Parse(reader.GetString(8)));

    private sealed record EmbeddingGeneration(Guid SessionId, string ProviderId, string ModelId, int ExpectedMemoryCount);
}

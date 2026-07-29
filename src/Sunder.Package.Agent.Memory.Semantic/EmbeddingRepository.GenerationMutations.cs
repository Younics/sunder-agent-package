using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed partial class EmbeddingRepository
{
    internal static void DeleteStagingGenerations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using (var embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText = """
                DELETE FROM SessionMemoryEmbeddings
                WHERE GenerationId IN (
                    SELECT GenerationId
                    FROM SessionMemoryEmbeddingGenerations
                    WHERE SessionId = $sessionId AND State = 'Staging'
                );
                """;
            embeddings.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            embeddings.ExecuteNonQuery();
        }

        using var generations = connection.CreateCommand();
        generations.Transaction = transaction;
        generations.CommandText = """
            DELETE FROM SessionMemoryEmbeddingGenerations
            WHERE SessionId = $sessionId AND State = 'Staging';
            """;
        generations.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        generations.ExecuteNonQuery();
    }

    private static string CreateCompleteGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string providerId,
        string modelId,
        string? configurationFingerprint)
    {
        var generationId = Guid.NewGuid().ToString("N");
        using (var generation = connection.CreateCommand())
        {
            generation.Transaction = transaction;
            generation.CommandText = """
                INSERT INTO SessionMemoryEmbeddingGenerations
                    (GenerationId, SessionId, ProviderId, ModelId, State, ExpectedMemoryCount, CreatedAtUtc, CompletedAtUtc,
                     ConfigurationFingerprint, SourceFingerprint, MaxCanonicalTextChars)
                VALUES ($generationId, $sessionId, $providerId, $modelId, 'Complete', 0, $now, $now,
                        $configurationFingerprint, NULL, NULL);
                """;
            generation.Parameters.AddWithValue("$generationId", generationId);
            generation.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            generation.Parameters.AddWithValue("$configurationFingerprint", (object?)configurationFingerprint ?? DBNull.Value);
            BindModel(generation, sessionId, providerId, modelId);
            if (generation.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Embedding generation '{generationId}' could not be created.");
            }
        }

        using var activate = connection.CreateCommand();
        activate.Transaction = transaction;
        activate.CommandText = """
            INSERT INTO SessionMemoryActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
            VALUES ($sessionId, $providerId, $modelId, $generationId)
            ON CONFLICT(SessionId, ProviderId, ModelId) DO UPDATE SET GenerationId = excluded.GenerationId;
            """;
        activate.Parameters.AddWithValue("$generationId", generationId);
        BindModel(activate, sessionId, providerId, modelId);
        if (activate.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Embedding generation '{generationId}' could not be activated.");
        }
        return generationId;
    }
}

using Microsoft.Data.Sqlite;
using static Sunder.Package.Agent.Memory.Semantic.MemoryRepositorySql;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore
{
    private static IReadOnlyList<StoredMemoryRecord> ListMergeCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {DurableMemoryColumns}
            FROM SessionMemories memory
            WHERE memory.SessionId = $sessionId
              AND memory.State = $state
            ORDER BY memory.CreatedAtUtc, memory.MemoryId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$state", ActiveState);
        using var reader = command.ExecuteReader();
        var memories = new List<StoredMemoryRecord>();
        while (reader.Read())
        {
            memories.Add(Read(reader));
        }
        return memories;
    }

    private static StoredMemoryRecord? FindMemoryByIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string category,
        string normalizedContent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {DurableMemoryColumns}
            FROM SessionMemories
            WHERE SessionId = $sessionId
              AND Category = $category COLLATE NOCASE
              AND NormalizedContent = $normalizedContent
            ORDER BY IsManual DESC, IsPinned DESC, UpdatedAtUtc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$normalizedContent", normalizedContent);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
}

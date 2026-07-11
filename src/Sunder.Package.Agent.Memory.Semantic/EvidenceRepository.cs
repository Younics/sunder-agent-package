using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class EvidenceRepository(string databasePath)
{
    private readonly string _databasePath = databasePath;

    public IReadOnlyList<StoredMemoryEvidenceRecord> List(Guid memoryId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
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

    internal void Insert(
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

    internal static void DeleteSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SessionMemoryEvidence WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.ExecuteNonQuery();
    }
}

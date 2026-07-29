using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class EvidenceRepository(string databasePath)
{
    internal const int MaxEvidenceRecordsPerMemory = 16;
    internal const int MaxEvidenceChars = 4_096;
    private readonly string _databasePath = databasePath;

    public IReadOnlyList<StoredMemoryEvidenceRecord> List(Guid memoryId)
    {
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EvidenceId, MemoryId, SessionId, SourceTurnId, EvidenceText, CreatedAtUtc, ContributionId
            FROM SessionMemoryEvidence
            WHERE MemoryId = $memoryId
            ORDER BY CreatedAtUtc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$limit", MaxEvidenceRecordsPerMemory);
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
                DateTimeOffset.Parse(reader.GetString(5)))
            {
                ContributionId = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
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
        var boundedEvidence = string.IsNullOrWhiteSpace(evidenceText)
            ? null
            : evidenceText.Trim()[..Math.Min(evidenceText.Trim().Length, MaxEvidenceChars)];
        command.Parameters.AddWithValue("$evidenceText", (object?)boundedEvidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Evidence for memory '{memoryId}' could not be persisted.");
        }

        using var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = """
            DELETE FROM SessionMemoryEvidence
            WHERE EvidenceId IN (
                SELECT EvidenceId
                FROM SessionMemoryEvidence
                WHERE MemoryId = $memoryId
                ORDER BY CreatedAtUtc DESC, EvidenceId DESC
                LIMIT -1 OFFSET $limit
            );
            """;
        trim.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        trim.Parameters.AddWithValue("$limit", MaxEvidenceRecordsPerMemory);
        trim.ExecuteNonQuery();
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

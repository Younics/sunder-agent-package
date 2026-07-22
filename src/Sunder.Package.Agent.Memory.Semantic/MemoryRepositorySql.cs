using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal static class MemoryRepositorySql
{
    private const string Columns =
        "MemoryId, SessionId, Category, Content, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount, Provenance";

    public static void Write(SqliteConnection connection, SqliteTransaction transaction, StoredMemoryRecord memory, string normalized, bool insert)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? """
                INSERT INTO SessionMemories
                    (MemoryId, SessionId, Category, Content, NormalizedContent, EvidenceText, SourceTurnId, Importance, Confidence, IsPinned, State, SupersededByMemoryId, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount, Provenance)
                VALUES ($memoryId, $sessionId, $category, $content, $normalized, $evidenceText, $sourceTurnId, $importance, $confidence, $isPinned, $state, $supersededByMemoryId, $createdAtUtc, $updatedAtUtc, $lastAccessedAtUtc, $accessCount, $provenance);
                """
            : """
                UPDATE SessionMemories SET Category = $category, Content = $content, NormalizedContent = $normalized,
                    EvidenceText = $evidenceText, SourceTurnId = $sourceTurnId, Importance = $importance, Confidence = $confidence,
                    IsPinned = $isPinned, State = $state, SupersededByMemoryId = $supersededByMemoryId,
                    UpdatedAtUtc = $updatedAtUtc, LastAccessedAtUtc = $lastAccessedAtUtc, AccessCount = $accessCount,
                    Provenance = $provenance
                WHERE MemoryId = $memoryId;
                """;
        Bind(command, memory, normalized);
        command.ExecuteNonQuery();
    }

    public static void WriteSearch(SqliteConnection connection, SqliteTransaction transaction, StoredMemoryRecord memory)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM SessionMemorySearch WHERE MemoryId = $memoryId;
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

    public static StoredMemoryRecord Read(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            Convert.ToSingle(reader.GetDouble(6)), Convert.ToSingle(reader.GetDouble(7)), reader.GetInt64(8) != 0,
            reader.GetString(9), reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)), DateTimeOffset.Parse(reader.GetString(11)),
            DateTimeOffset.Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)), reader.GetInt32(14),
            Enum.TryParse<Sunder.Package.Agent.Contracts.Models.AgentMemoryProvenance>(reader.GetString(15), ignoreCase: true, out var provenance)
                ? provenance
                : Sunder.Package.Agent.Contracts.Models.AgentMemoryProvenance.Unknown);

    public static string AliasedColumns(string alias)
        => string.Join(", ", Columns.Split(", ").Select(column => $"{alias}.{column}"));

    public static string Normalize(string text)
        => string.Join(' ', text.Trim().ToLowerInvariant().Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    public static string? BuildFtsMatchQuery(string text)
    {
        var normalized = Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        var clauses = tokens.Length > 1 ? new List<string> { $"\"{normalized}\"" } : [];
        clauses.AddRange(tokens.Select(token => token + "*"));
        return string.Join(" OR ", clauses);
    }

    private static void Bind(SqliteCommand command, StoredMemoryRecord memory, string normalized)
    {
        command.Parameters.AddWithValue("$memoryId", memory.MemoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", memory.SessionId.ToString());
        command.Parameters.AddWithValue("$category", memory.Category);
        command.Parameters.AddWithValue("$content", memory.Content);
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue("$evidenceText", (object?)memory.EvidenceText ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceTurnId", memory.SourceTurnId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$importance", memory.Importance);
        command.Parameters.AddWithValue("$confidence", memory.Confidence);
        command.Parameters.AddWithValue("$isPinned", memory.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$state", memory.State);
        command.Parameters.AddWithValue("$supersededByMemoryId", memory.SupersededByMemoryId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", memory.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", memory.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$lastAccessedAtUtc", memory.LastAccessedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$accessCount", memory.AccessCount);
        command.Parameters.AddWithValue("$provenance", memory.Provenance.ToString());
    }
}

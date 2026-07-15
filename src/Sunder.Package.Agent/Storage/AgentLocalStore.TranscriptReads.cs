using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListTurns(connection, sessionId);
    }

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListRecentTurnHeadersForSession(connection, sessionId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        var items = ListTurnItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray());
        return AttachItems(turns, items);
    }

    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListTurnHeadersBefore(connection, sessionId, beforeCreatedAtUtc, beforeTurnId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        var items = ListTurnItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray());
        return AttachItems(turns, items);
    }

    public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListTurnHeadersAfter(connection, sessionId, afterCreatedAtUtc, afterTurnId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId)
            .ToArray();
        var items = ListTurnItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray());
        return AttachItems(turns, items);
    }

    public IReadOnlyList<AgentTranscriptMessageRecord> ListMessages(Guid sessionId)
    {
        return ListTurns(sessionId)
            .Select(ProjectTurnToTranscriptMessage)
            .ToArray();
    }

    public AgentTranscriptMessageRecord? GetMessage(Guid messageId)
    {
        using var connection = CreateConnection();
        connection.Open();

        var turn = GetTurn(connection, messageId);
        return turn is null ? null : ProjectTurnToTranscriptMessage(turn);
    }

    public AgentTurnRecord? GetTurn(Guid turnId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetTurn(connection, turnId);
    }

    private static IReadOnlyList<AgentTurnRecord> ListTurns(SqliteConnection connection, Guid sessionId)
    {
        var turns = ListTurnHeadersForSession(connection, sessionId, descending: false);
        var items = ListTurnItemsForSession(connection, sessionId);
        return AttachItems(turns, items);
    }

    private static AgentTurnRecord? GetTurn(SqliteConnection connection, Guid turnId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc FROM AgentTurns WHERE TurnId = $id;";
        command.Parameters.AddWithValue("$id", turnId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var turn = ReadTurnHeader(reader);
        var items = ListTurnItemsForTurns(connection, [turnId]);
        return AttachItems([turn], items).Single();
    }

    private static IReadOnlyList<AgentTurnRecord> ListTurnHeadersForSession(SqliteConnection connection, Guid sessionId, bool descending)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc FROM AgentTurns WHERE SessionId = $sessionId ORDER BY CreatedAtUtc {(descending ? "DESC" : "ASC")}, TurnId {(descending ? "DESC" : "ASC")};";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        var turns = new List<AgentTurnRecord>();
        while (reader.Read())
        {
            turns.Add(ReadTurnHeader(reader));
        }

        return turns;
    }

    private static IReadOnlyList<AgentTurnRecord> ListRecentTurnHeadersForSession(
        SqliteConnection connection,
        Guid sessionId,
        int limit,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc FROM AgentTurns WHERE SessionId = $sessionId ORDER BY CreatedAtUtc DESC, TurnId DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));

        using var reader = command.ExecuteReader();
        var turns = new List<AgentTurnRecord>();
        while (reader.Read())
        {
            turns.Add(ReadTurnHeader(reader));
        }

        return turns;
    }

    private static IReadOnlyList<AgentTurnRecord> ListTurnHeadersBefore(
        SqliteConnection connection,
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc < $beforeCreatedAtUtc OR (CreatedAtUtc = $beforeCreatedAtUtc AND TurnId < $beforeTurnId))
            ORDER BY CreatedAtUtc DESC, TurnId DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$beforeCreatedAtUtc", beforeCreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$beforeTurnId", beforeTurnId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));

        using var reader = command.ExecuteReader();
        var turns = new List<AgentTurnRecord>();
        while (reader.Read())
        {
            turns.Add(ReadTurnHeader(reader));
        }

        return turns;
    }

    private static IReadOnlyList<AgentTurnRecord> ListTurnHeadersAfter(
        SqliteConnection connection,
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc > $afterCreatedAtUtc OR (CreatedAtUtc = $afterCreatedAtUtc AND TurnId > $afterTurnId))
            ORDER BY CreatedAtUtc ASC, TurnId ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$afterCreatedAtUtc", afterCreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$afterTurnId", afterTurnId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));

        using var reader = command.ExecuteReader();
        var turns = new List<AgentTurnRecord>();
        while (reader.Read())
        {
            turns.Add(ReadTurnHeader(reader));
        }

        return turns;
    }

    private static IReadOnlyList<AgentTurnRecord> ListRecentTurnHeaders(SqliteConnection connection, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc FROM AgentTurns ORDER BY CreatedAtUtc DESC, TurnId DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var turns = new List<AgentTurnRecord>();
        while (reader.Read())
        {
            turns.Add(ReadTurnHeader(reader));
        }

        return turns;
    }

    private static IReadOnlyList<AgentTurnItemRecord> ListTurnItemsForSession(SqliteConnection connection, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.ItemId, i.TurnId, i.SequenceNumber, i.Kind, i.TextContent, i.CallId, i.ToolId, i.ArgumentsJson, i.ResultSummary, i.StructuredPayloadJson, i.SourcesJson, i.WasTruncated, i.IsError, i.ErrorCode, i.BackendId, i.PresentationPayloadJson
            FROM AgentTurnItems i
            INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
            WHERE t.SessionId = $sessionId
            ORDER BY t.CreatedAtUtc, t.TurnId, i.SequenceNumber;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        var items = new List<AgentTurnItemRecord>();
        while (reader.Read())
        {
            items.Add(ReadTurnItem(reader));
        }

        return items;
    }

    private static IReadOnlyList<AgentTurnItemRecord> ListTurnItemsForTurns(
        SqliteConnection connection,
        IReadOnlyList<Guid> turnIds,
        SqliteTransaction? transaction = null)
    {
        if (turnIds.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new List<string>(turnIds.Count);
        for (var index = 0; index < turnIds.Count; index++)
        {
            var parameterName = $"$turnId{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, turnIds[index].ToString());
        }

        command.CommandText = $"SELECT ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId, ArgumentsJson, ResultSummary, StructuredPayloadJson, SourcesJson, WasTruncated, IsError, ErrorCode, BackendId, PresentationPayloadJson FROM AgentTurnItems WHERE TurnId IN ({string.Join(", ", parameterNames)}) ORDER BY TurnId, SequenceNumber;";

        using var reader = command.ExecuteReader();
        var items = new List<AgentTurnItemRecord>();
        while (reader.Read())
        {
            items.Add(ReadTurnItem(reader));
        }

        return items;
    }

    private static IReadOnlyList<AgentTurnRecord> AttachItems(
        IReadOnlyList<AgentTurnRecord> turns,
        IReadOnlyList<AgentTurnItemRecord> items)
    {
        if (turns.Count == 0)
        {
            return turns;
        }

        var itemsByTurnId = items
            .GroupBy(item => item.TurnId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AgentTurnItemRecord>)group.OrderBy(item => item.SequenceNumber).ToArray());

        return turns
            .Select(turn => turn with { Items = itemsByTurnId.TryGetValue(turn.TurnId, out var turnItems) ? turnItems : [] })
            .ToArray();
    }

    private static AgentTurnRecord ReadTurnHeader(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Enum.Parse<AgentMessageRole>(reader.GetString(2), ignoreCase: true),
            Enum.Parse<AgentTurnKind>(reader.GetString(3), ignoreCase: true),
            [],
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)));

    private static AgentTurnItemRecord ReadTurnItem(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            Enum.Parse<AgentTurnItemKind>(reader.GetString(3), ignoreCase: true),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11) != 0,
            reader.GetInt64(12) != 0,
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));
}

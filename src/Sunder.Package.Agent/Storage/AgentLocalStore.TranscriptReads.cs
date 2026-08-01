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
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var items = ListTurnItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray());
        return AttachItems(turns, items);
    }

    internal IReadOnlyList<AgentTurnRecord> ListRecentTranscriptHeaders(Guid sessionId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListRecentTurnHeadersForSession(connection, sessionId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        return AttachItems(
            turns,
            ListTranscriptHeaderItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray()));
    }

    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListTurnHeadersBefore(connection, sessionId, beforeCreatedAtUtc, beforeTurnId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var items = ListTurnItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray());
        return AttachItems(turns, items);
    }

    internal IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersBefore(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListTurnHeadersBefore(connection, sessionId, beforeCreatedAtUtc, beforeTurnId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        return AttachItems(
            turns,
            ListTranscriptHeaderItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray()));
    }

    public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListTurnHeadersAfter(connection, sessionId, afterCreatedAtUtc, afterTurnId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var items = ListTurnItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray());
        return AttachItems(turns, items);
    }

    internal IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersAfter(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        var turns = ListTurnHeadersAfter(connection, sessionId, afterCreatedAtUtc, afterTurnId, limit)
            .OrderBy(turn => turn.CreatedAtUtc)
            .ThenBy(turn => turn.TurnId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        return AttachItems(
            turns,
            ListTranscriptHeaderItemsForTurns(connection, turns.Select(turn => turn.TurnId).ToArray()));
    }

    public AgentTurnRecord? GetTurn(Guid turnId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetTurn(connection, turnId);
    }

    internal AgentTurnRecord? GetTranscriptHeader(Guid turnId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetTranscriptHeader(connection, turnId);
    }

    private static IReadOnlyList<AgentTurnRecord> ListTurns(
        SqliteConnection connection,
        Guid sessionId,
        SqliteTransaction? transaction = null)
    {
        var turns = ListTurnHeadersForSession(connection, sessionId, descending: false, transaction);
        var items = ListTurnItemsForSession(connection, sessionId, transaction);
        return AttachItems(turns, items);
    }

    private static AgentTurnRecord? GetTurn(
        SqliteConnection connection,
        Guid turnId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns WHERE TurnId = $id;";
        command.Parameters.AddWithValue("$id", turnId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var turn = ReadTurnHeader(reader);
        var items = ListTurnItemsForTurns(connection, [turnId], transaction);
        return AttachItems([turn], items).Single();
    }

    private static AgentTurnRecord? GetTranscriptHeader(
        SqliteConnection connection,
        Guid turnId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns WHERE TurnId = $id;";
        command.Parameters.AddWithValue("$id", turnId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var turn = ReadTurnHeader(reader);
        return AttachItems(
            [turn],
            ListTranscriptHeaderItemsForTurns(connection, [turnId], transaction)).Single();
    }

    private static IReadOnlyList<AgentTurnRecord> ListTurnHeadersForSession(
        SqliteConnection connection,
        Guid sessionId,
        bool descending,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns WHERE SessionId = $sessionId ORDER BY CreatedAtUtc COLLATE BINARY {(descending ? "DESC" : "ASC")}, TurnId COLLATE BINARY {(descending ? "DESC" : "ASC")};";
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
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns WHERE SessionId = $sessionId ORDER BY CreatedAtUtc COLLATE BINARY DESC, TurnId COLLATE BINARY DESC LIMIT $limit;";
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
            SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc COLLATE BINARY < $beforeCreatedAtUtc COLLATE BINARY OR (CreatedAtUtc = $beforeCreatedAtUtc AND TurnId COLLATE BINARY < $beforeTurnId COLLATE BINARY))
            ORDER BY CreatedAtUtc COLLATE BINARY DESC, TurnId COLLATE BINARY DESC
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
            SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc COLLATE BINARY > $afterCreatedAtUtc COLLATE BINARY OR (CreatedAtUtc = $afterCreatedAtUtc AND TurnId COLLATE BINARY > $afterTurnId COLLATE BINARY))
            ORDER BY CreatedAtUtc COLLATE BINARY ASC, TurnId COLLATE BINARY ASC
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
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns ORDER BY CreatedAtUtc COLLATE BINARY DESC, TurnId COLLATE BINARY DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var turns = new List<AgentTurnRecord>();
        while (reader.Read())
        {
            turns.Add(ReadTurnHeader(reader));
        }

        return turns;
    }

    private static IReadOnlyList<AgentTurnItemRecord> ListTurnItemsForSession(
        SqliteConnection connection,
        Guid sessionId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.ItemId, i.TurnId, i.SequenceNumber, i.Kind, i.TextContent, i.CallId, i.ToolId, i.ArgumentsJson, i.ResultSummary, i.StructuredPayloadJson, i.SourcesJson, i.WasTruncated, i.IsError, i.ErrorCode, i.BackendId, i.PresentationPayloadJson, i.ToolExecutionId, e.Status, e.OwnerPackageId, e.ToolSchemaId, e.ToolSchemaVersion
            FROM AgentTurnItems i
            INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
            LEFT JOIN AgentToolExecutions e ON e.ExecutionId = i.ToolExecutionId
            WHERE t.SessionId = $sessionId
            ORDER BY t.CreatedAtUtc COLLATE BINARY, t.TurnId COLLATE BINARY, i.SequenceNumber;
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

        command.CommandText = $"SELECT i.ItemId, i.TurnId, i.SequenceNumber, i.Kind, i.TextContent, i.CallId, i.ToolId, i.ArgumentsJson, i.ResultSummary, i.StructuredPayloadJson, i.SourcesJson, i.WasTruncated, i.IsError, i.ErrorCode, i.BackendId, i.PresentationPayloadJson, i.ToolExecutionId, e.Status, e.OwnerPackageId, e.ToolSchemaId, e.ToolSchemaVersion FROM AgentTurnItems i LEFT JOIN AgentToolExecutions e ON e.ExecutionId = i.ToolExecutionId WHERE i.TurnId IN ({string.Join(", ", parameterNames)}) ORDER BY i.TurnId, i.SequenceNumber;";

        using var reader = command.ExecuteReader();
        var items = new List<AgentTurnItemRecord>();
        while (reader.Read())
        {
            items.Add(ReadTurnItem(reader));
        }

        return items;
    }

    private static IReadOnlyList<AgentTurnItemRecord> ListTranscriptHeaderItemsForTurns(
        SqliteConnection connection,
        IReadOnlyList<Guid> turnIds,
        SqliteTransaction? transaction = null)
    {
        if (turnIds.Count == 0)
        {
            return [];
        }

        var items = new List<AgentTurnItemRecord>();
        using (var contentCommand = connection.CreateCommand())
        {
            contentCommand.Transaction = transaction;
            var turnParameters = AddTurnIdParameters(contentCommand, turnIds, "contentTurnId");
            contentCommand.CommandText = $"""
                SELECT ItemId, TurnId, SequenceNumber, Kind, TextContent,
                       CASE WHEN Kind = 'Attachment' THEN StructuredPayloadJson ELSE NULL END,
                       WasTruncated, IsError
                FROM AgentTurnItems
                WHERE TurnId IN ({string.Join(", ", turnParameters)})
                  AND Kind NOT IN ('ToolCall', 'ToolResult')
                ORDER BY TurnId, SequenceNumber;
                """;
            using var reader = contentCommand.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new AgentTurnItemRecord(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    Enum.Parse<AgentTurnItemKind>(reader.GetString(3), ignoreCase: true),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    CallId: null,
                    ToolId: null,
                    ArgumentsJson: null,
                    ResultSummary: null,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    SourcesJson: null,
                    reader.GetInt64(6) != 0,
                    reader.GetInt64(7) != 0,
                    ErrorCode: null,
                    BackendId: null));
            }
        }

        using (var toolCommand = connection.CreateCommand())
        {
            toolCommand.Transaction = transaction;
            var turnParameters = AddTurnIdParameters(toolCommand, turnIds, "toolTurnId");
            const string executionId = "COALESCE(i.ToolExecutionId, e.ExecutionId)";
            var peerPredicate = $"""
                (({executionId} IS NOT NULL AND peer.ToolExecutionId = {executionId})
                 OR ({executionId} IS NULL
                     AND owner.RunId IS NOT NULL
                     AND owner.RunRevision IS NOT NULL
                     AND peerTurn.SessionId = owner.SessionId
                     AND peerTurn.RunId = owner.RunId
                     AND peerTurn.RunRevision = owner.RunRevision
                     AND peer.CallId = i.CallId)
                 OR ({executionId} IS NULL
                     AND owner.RunId IS NULL
                     AND peer.ItemId = i.ItemId))
                """;
            toolCommand.CommandText = $"""
                SELECT i.ItemId,
                       i.TurnId,
                       i.SequenceNumber,
                       i.Kind,
                       substr(i.CallId, 1, 1024),
                       substr(i.ToolId, 1, 1024),
                       i.WasTruncated,
                       {executionId},
                       COALESCE(
                           e.Status,
                           (
                               SELECT CASE WHEN peer.IsError = 1 THEN 'Failed' ELSE 'Completed' END
                               FROM AgentTurnItems peer
                               INNER JOIN AgentTurns peerTurn ON peerTurn.TurnId = peer.TurnId
                               WHERE peer.Kind = 'ToolResult'
                                 AND {peerPredicate}
                               ORDER BY peerTurn.CreatedAtUtc COLLATE BINARY DESC,
                                        peerTurn.TurnId COLLATE BINARY DESC,
                                        peer.SequenceNumber DESC
                               LIMIT 1
                           ),
                           CASE WHEN i.Kind = 'ToolResult'
                                THEN CASE WHEN i.IsError = 1 THEN 'Failed' ELSE 'Completed' END
                                ELSE 'Started' END),
                       substr(COALESCE(
                           e.OutcomeSummary,
                           (
                               SELECT COALESCE(
                                   peer.ResultSummary,
                                   CASE WHEN peer.IsError = 1 THEN peer.TextContent ELSE NULL END,
                                   peer.ErrorCode)
                               FROM AgentTurnItems peer
                               INNER JOIN AgentTurns peerTurn ON peerTurn.TurnId = peer.TurnId
                               WHERE peer.Kind = 'ToolResult'
                                 AND {peerPredicate}
                               ORDER BY peerTurn.CreatedAtUtc COLLATE BINARY DESC,
                                        peerTurn.TurnId COLLATE BINARY DESC,
                                        peer.SequenceNumber DESC
                               LIMIT 1
                           )), 1, 240),
                       e.UpdatedAtUtc,
                       (
                           SELECT MAX(peerTurn.UpdatedAtUtc)
                           FROM AgentTurnItems peer
                           INNER JOIN AgentTurns peerTurn ON peerTurn.TurnId = peer.TurnId
                           WHERE peer.Kind IN ('ToolCall', 'ToolResult')
                              AND {peerPredicate}
                       ),
                       owner.UpdatedAtUtc,
                       EXISTS (
                           SELECT 1
                           FROM AgentTurnItems peer
                           INNER JOIN AgentTurns peerTurn ON peerTurn.TurnId = peer.TurnId
                           WHERE peer.Kind IN ('ToolCall', 'ToolResult')
                             AND {peerPredicate}
                             AND (
                                 peer.IsError = 1
                                 OR (length(trim(peer.ArgumentsJson)) > 0
                                     AND trim(peer.ArgumentsJson) <> (char(123) || char(125)))
                                 OR length(trim(peer.TextContent)) > 0
                                 OR length(trim(peer.ResultSummary)) > 0
                                 OR length(trim(peer.StructuredPayloadJson)) > 0
                                 OR length(trim(peer.SourcesJson)) > 0
                                 OR length(trim(peer.PresentationPayloadJson)) > 0
                                 OR length(trim(peer.ErrorCode)) > 0
                                 OR length(trim(peer.BackendId)) > 0
                             )
                       )
                FROM AgentTurnItems i
                INNER JOIN AgentTurns owner ON owner.TurnId = i.TurnId
                LEFT JOIN AgentToolExecutions e
                  ON e.ExecutionId = i.ToolExecutionId
                  OR (i.ToolExecutionId IS NULL
                      AND owner.RunId IS NOT NULL
                      AND owner.RunRevision IS NOT NULL
                      AND e.SessionId = owner.SessionId
                      AND e.RunId = owner.RunId
                      AND e.RunRevision = owner.RunRevision
                      AND e.CallId = i.CallId)
                WHERE i.TurnId IN ({string.Join(", ", turnParameters)})
                  AND i.Kind IN ('ToolCall', 'ToolResult')
                ORDER BY i.TurnId, i.SequenceNumber;
                """;
            using var reader = toolCommand.ExecuteReader();
            while (reader.Read())
            {
                var status = Enum.Parse<AgentToolExecutionStatus>(reader.GetString(8), ignoreCase: true);
                var summary = reader.IsDBNull(9) ? null : reader.GetString(9);
                var isFailure = status is AgentToolExecutionStatus.Failed or AgentToolExecutionStatus.Ambiguous;
                items.Add(new AgentTurnItemRecord(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    Enum.Parse<AgentTurnItemKind>(reader.GetString(3), ignoreCase: true),
                    TextContent: null,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    ArgumentsJson: null,
                    ResultSummary: null,
                    StructuredPayloadJson: null,
                    SourcesJson: null,
                    reader.GetInt64(6) != 0,
                    IsError: status == AgentToolExecutionStatus.Failed,
                    ErrorCode: null,
                    BackendId: null)
                {
                    ToolExecutionId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                    ToolExecutionStatus = status,
                    ToolHeaderHint = isFailure ? null : summary,
                    ToolErrorSummary = isFailure ? summary : null,
                    ToolDetailRevision = CreateDetailRevision(
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetString(12)),
                    ToolHasDetails = status == AgentToolExecutionStatus.Ambiguous
                                     || reader.GetInt64(13) != 0,
                    IsToolHeaderProjection = true,
                });
            }
        }

        return items;
    }

    private static IReadOnlyList<string> AddTurnIdParameters(
        SqliteCommand command,
        IReadOnlyList<Guid> turnIds,
        string prefix)
    {
        var parameterNames = new string[turnIds.Count];
        for (var index = 0; index < turnIds.Count; index++)
        {
            var parameterName = $"${prefix}{index}";
            parameterNames[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, turnIds[index].ToString());
        }
        return parameterNames;
    }

    private static long CreateDetailRevision(params string?[] timestamps)
        => Math.Max(
            1,
            timestamps
                .Where(static timestamp => !string.IsNullOrWhiteSpace(timestamp))
                .Select(static timestamp => CreateProtocolTimestampRevision(DateTimeOffset.Parse(timestamp!)))
                .DefaultIfEmpty(1)
                .Max());

    private static long CreateProtocolTimestampRevision(DateTimeOffset timestamp)
        // Unix microseconds retain timestamp ordering while staying within the RPC safe-integer bound.
        => Math.Max(1, (timestamp.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);

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
            DateTimeOffset.Parse(reader.GetString(5)))
        {
            ContentRevision = reader.GetInt64(6),
            IsStreaming = reader.GetInt64(7) != 0,
            RunId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            RunRevision = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        };

    private static AgentTurnItemRecord ReadTurnItem(SqliteDataReader reader)
        => new AgentTurnItemRecord(
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
            reader.IsDBNull(15) ? null : reader.GetString(15))
        {
            ToolExecutionId = reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
            ToolExecutionStatus = reader.IsDBNull(17)
                ? null
                : Enum.Parse<AgentToolExecutionStatus>(reader.GetString(17), ignoreCase: true),
            ToolOwnerPackageId = reader.IsDBNull(18) ? null : reader.GetString(18),
            ToolSchemaId = reader.IsDBNull(19) ? null : reader.GetString(19),
            ToolSchemaVersion = reader.IsDBNull(20) ? null : reader.GetString(20),
        };

}

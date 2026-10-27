using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static (Guid Turn, long Sequence)? ResolveDetailedAnchor(SqliteConnection connection,
        SqliteTransaction transaction, Guid session, Guid turn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TurnId, TurnSequence FROM AgentTranscriptEntries WHERE SessionId = $session AND TurnId = $turn
            UNION ALL
            SELECT p.TurnId, p.TurnSequence FROM AgentTurnItems i
            INNER JOIN AgentTranscriptEntries p ON p.ExecutionId = i.ToolExecutionId
            WHERE p.SessionId = $session AND i.TurnId = $turn LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$turn", turn.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Guid.Parse(reader.GetString(0)), reader.GetInt64(1)) : null;
    }

    private IReadOnlyList<AgentTurnRecord> ReadDetailedTranscript(Guid session, int limit, Guid? anchor = null, bool before = true)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        // Empty is the explicit beginning boundary for around readers with no
        // predecessor. Missing identities never fall back to wall-clock order.
        var position = anchor == Guid.Empty ? ((Guid Turn, long Sequence)?)(Guid.Empty, 0)
            : anchor is { } turn ? ResolveDetailedAnchor(connection, transaction, session, turn) : null;
        IReadOnlyList<AgentTurnRecord> headers = anchor is not null && position is null ? []
            : ReadDetailedTranscript(connection, transaction, session, Math.Clamp(limit, 0, 501), position?.Sequence, before);
        transaction.Commit();
        return headers;
    }

    // A page spends its budget on messages/invocations, not their separate
    // authoritative call/result receipts. No payload or session-wide pairing is read.
    private static IReadOnlyList<AgentTurnRecord> ReadDetailedTranscript(SqliteConnection connection,
        SqliteTransaction transaction, Guid session, int limit, long? anchor = null, bool before = true)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT p.TurnId FROM AgentTranscriptEntries p
            WHERE p.SessionId = $session {(anchor is null ? "" : $"AND p.TurnSequence {(before ? "<" : ">")} $anchor")}
              AND p.EntryId = (SELECT first.EntryId FROM AgentTranscriptEntries first
                  WHERE first.TurnId = p.TurnId ORDER BY first.ItemSequence LIMIT 1)
            ORDER BY p.TurnSequence {(before ? "DESC" : "ASC")}, p.ItemSequence {(before ? "DESC" : "ASC")}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$anchor", (object?)anchor ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ReadDetailedHeaders(connection, transaction, ids);
    }

    private static IReadOnlyList<AgentTurnRecord> ReadDetailedHeaders(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<Guid> turns)
    {
        if (turns.Count == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = AddTurnIdParameters(command, turns, "logicalTurn");
        command.CommandText = $"SELECT EntryId FROM AgentTranscriptEntries WHERE TurnId IN ({string.Join(',', names)}) AND ExecutionId IS NOT NULL;";
        var items = new List<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) items.Add(Guid.Parse(reader.GetString(0)));
        return AttachItems(ReadCompactTurnHeaders(connection, transaction, turns).OrderBy(turn => turn.TurnSequence).ToArray(),
            ListTranscriptHeaderItemsForTurns(connection, turns, transaction, items));
    }
}

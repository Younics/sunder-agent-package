using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentCompactTranscriptPage ReadCompactTranscript(AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var page = ReadCompactTranscript(connection, transaction, request, cancellationToken);
        transaction.Commit();
        return page;
    }

    private static AgentCompactTranscriptPage ReadCompactTranscript(SqliteConnection connection, SqliteTransaction transaction,
        AgentCompactTranscriptRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var epoch = ReadCompactTranscriptEpoch(connection, transaction, request.SessionId);
        var limit = Math.Clamp(request.Limit, 1, 500);
        var anchor = request.AnchorTurnId is { } turnId
            ? ResolveCompactEntry(connection, transaction, request.SessionId, turnId, request.AnchorItemId) : null;
        var end = request.EndTurnId is { } endTurn
            ? ResolveCompactEntry(connection, transaction, request.SessionId, endTurn, null) : null;
        if (request.Direction != AgentCompactTranscriptDirection.Recent && anchor is null)
            throw new InvalidOperationException("The transcript anchor is no longer available.");
        var entries = request.Direction switch
        {
            AgentCompactTranscriptDirection.Recent => ReadCompactEntries(connection, transaction, request.SessionId, epoch, "1 = 1", long.MaxValue, limit, true),
            AgentCompactTranscriptDirection.Before => ReadCompactEntries(connection, transaction, request.SessionId, epoch, "p.TurnSequence < $anchor", anchor!.Value.Sequence, limit, true),
            AgentCompactTranscriptDirection.After => ReadCompactEntries(connection, transaction, request.SessionId, epoch, "p.TurnSequence > $anchor", anchor!.Value.Sequence, limit, false),
            AgentCompactTranscriptDirection.Around => ReadCompactEntries(connection, transaction, request.SessionId, epoch, "p.TurnSequence < $anchor", anchor!.Value.Sequence, limit / 2, true)
                .Concat(ReadCompactEntries(connection, transaction, request.SessionId, epoch, "p.TurnSequence >= $anchor", anchor.Value.Sequence, limit - limit / 2, false)).ToList(),
            AgentCompactTranscriptDirection.Window when end is not null => ReadCompactEntries(connection, transaction, request.SessionId, epoch,
                "p.TurnSequence >= $anchor AND p.TurnSequence <= $end", anchor!.Value.Sequence, limit, false, end.Value.Sequence),
            _ => throw new ArgumentException("Invalid transcript page direction or boundary."),
        };
        token.ThrowIfCancellationRequested();
        var messages = ReadCompactMessageHeaders(connection, transaction, entries.Where(entry => entry.Group is null).Select(entry => entry.TurnId).ToArray())
            .ToDictionary(turn => turn.TurnId);
        var tools = ReadGroupTools(connection, transaction, entries.Select(entry => entry.Group?.First.ItemId).OfType<Guid>().ToArray());
        entries = entries.Select(entry => entry.Group is { } group
            ? entry with { Group = group with { Tools = tools.GetValueOrDefault(group.First.ItemId) ?? [] } }
            : entry with { Message = messages[entry.TurnId] }).ToList();
        var first = entries.FirstOrDefault()?.TurnSequence;
        var last = entries.LastOrDefault()?.TurnSequence;
        using var boundaries = connection.CreateCommand();
        boundaries.Transaction = transaction;
        boundaries.CommandText = """
            SELECT EXISTS (SELECT 1 FROM AgentTranscriptEntries WHERE SessionId = $session AND CompactVisible = 1 AND TurnSequence < $first),
                   EXISTS (SELECT 1 FROM AgentTranscriptEntries WHERE SessionId = $session AND CompactVisible = 1 AND TurnSequence > $last);
            """;
        boundaries.Parameters.AddWithValue("$session", request.SessionId.ToString());
        boundaries.Parameters.AddWithValue("$first", (object?)first ?? DBNull.Value);
        boundaries.Parameters.AddWithValue("$last", (object?)last ?? DBNull.Value);
        using var reader = boundaries.ExecuteReader();
        reader.Read();
        return new(entries, reader.GetInt64(0) != 0, reader.GetInt64(1) != 0,
            request.Direction == AgentCompactTranscriptDirection.Around ? anchor?.Entry : null) { TranscriptEpoch = epoch };
    }

    private static List<AgentCompactTranscriptEntry> ReadCompactEntries(SqliteConnection connection, SqliteTransaction transaction,
        Guid session, long epoch, string predicate, long anchor, int limit, bool descending, long end = long.MaxValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT p.EntryId, p.TurnId, p.TurnSequence, p.ItemSequence, t.CreatedAtUtc, t.RunId, t.RunRevision,
                   g.LastEntryId, g.Revision, g.RunningCount, g.CompletedCount, g.FailedCount,
                   last.TurnId, last.TurnSequence, last.ItemSequence, lastTurn.CreatedAtUtc
            FROM AgentTranscriptEntries p INNER JOIN AgentTurns t ON t.TurnId = p.TurnId
            LEFT JOIN AgentTranscriptGroups g ON g.GroupId = p.GroupId
            LEFT JOIN AgentTranscriptEntries last ON last.EntryId = g.LastEntryId
            LEFT JOIN AgentTurns lastTurn ON lastTurn.TurnId = last.TurnId
            WHERE p.SessionId = $session AND p.CompactVisible = 1 AND {predicate}
            ORDER BY p.TurnSequence {(descending ? "DESC" : "ASC")}, p.ItemSequence {(descending ? "DESC" : "ASC")} LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$anchor", anchor);
        command.Parameters.AddWithValue("$end", end);
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<AgentCompactTranscriptEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entry = Guid.Parse(reader.GetString(0));
            var turn = Guid.Parse(reader.GetString(1));
            var sequence = reader.GetInt64(2);
            var created = DateTimeOffset.Parse(reader.GetString(4));
            AgentTranscriptActivityGroup? group = reader.IsDBNull(7) ? null : new(session,
                new(created, turn, reader.GetInt32(3), entry) { TurnSequence = sequence },
                new(DateTimeOffset.Parse(reader.GetString(15)), Guid.Parse(reader.GetString(12)), reader.GetInt32(14), Guid.Parse(reader.GetString(7)))
                    { TurnSequence = reader.GetInt64(13) },
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                epoch, reader.GetInt64(8), [], reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11));
            entries.Add(new(entry, created, turn, null, group) { TurnSequence = sequence });
        }
        if (descending) entries.Reverse();
        return entries;
    }

    private static (Guid Entry, long Sequence)? ResolveCompactEntry(SqliteConnection connection, SqliteTransaction transaction,
        Guid session, Guid turn, Guid? item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Resolve the exact receipt first; a result's presentation position belongs
        // to its invocation, not the time at which that result arrived.
        command.CommandText = """
            SELECT COALESCE(p.GroupId, p.EntryId), first.TurnSequence
            FROM AgentTranscriptEntries p
            INNER JOIN AgentTranscriptEntries first ON first.EntryId = COALESCE(p.GroupId, p.EntryId)
            WHERE p.SessionId = $session AND p.EntryId = $turn
            UNION ALL
            SELECT COALESCE(p.GroupId, p.EntryId), first.TurnSequence
            FROM AgentTurnItems i INNER JOIN AgentTranscriptEntries p ON p.ExecutionId = i.ToolExecutionId
            INNER JOIN AgentTranscriptEntries first ON first.EntryId = COALESCE(p.GroupId, p.EntryId)
            WHERE p.SessionId = $session AND i.TurnId = $turn AND ($item IS NULL OR i.ItemId = $item)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$turn", turn.ToString());
        command.Parameters.AddWithValue("$item", item?.ToString() ?? (object)DBNull.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Guid.Parse(reader.GetString(0)), reader.GetInt64(1)) : null;
    }

    private static Dictionary<Guid, IReadOnlyList<AgentTranscriptToolCount>> ReadGroupTools(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<Guid> groups)
    {
        if (groups.Count == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = AddTurnIdParameters(command, groups, "group");
        command.CommandText = $"SELECT GroupId, ToolId, Count FROM AgentTranscriptGroupTools WHERE GroupId IN ({string.Join(',', names)}) ORDER BY FirstTurnSequence, FirstItemSequence;";
        var tools = new Dictionary<Guid, List<AgentTranscriptToolCount>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!tools.TryGetValue(id, out var list)) tools.Add(id, list = []);
            list.Add(new(reader.GetString(1), reader.GetInt64(2)));
        }
        return tools.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AgentTranscriptToolCount>)pair.Value);
    }

    internal AgentTranscriptActivityMembersPage ReadActivityMembers(AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ReadCompactTranscriptEpoch(connection, transaction, request.SessionId) != request.TranscriptEpoch)
            throw new InvalidOperationException("Activity changed. Reopen the group to load its current calls.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.EntryId, p.TurnId, p.TurnSequence, p.ItemSequence, t.CreatedAtUtc
            FROM AgentTranscriptEntries p INNER JOIN AgentTurns t ON t.TurnId = p.TurnId
            WHERE p.SessionId = $session AND p.GroupId = $group
              AND (p.TurnSequence, p.ItemSequence) <= (
                  SELECT TurnSequence, ItemSequence FROM AgentTranscriptEntries WHERE EntryId = $through AND GroupId = $group AND SessionId = $session)
              AND ($after IS NULL OR (p.TurnSequence, p.ItemSequence) > (
                  SELECT TurnSequence, ItemSequence FROM AgentTranscriptEntries WHERE EntryId = $after AND GroupId = $group AND SessionId = $session))
            ORDER BY p.TurnSequence, p.ItemSequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", request.SessionId.ToString());
        command.Parameters.AddWithValue("$group", request.FirstItemId.ToString());
        command.Parameters.AddWithValue("$through", request.Through.ItemId.ToString());
        command.Parameters.AddWithValue("$after", request.After?.ItemId.ToString() ?? (object)DBNull.Value);
        var limit = Math.Clamp(request.Limit, 1, 200);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var positions = new List<AgentTranscriptInvocationCursor>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) positions.Add(new(DateTimeOffset.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(1)), reader.GetInt32(3), Guid.Parse(reader.GetString(0)))
                { TurnSequence = reader.GetInt64(2) });
        if (positions.Count == 0 && request.After?.ItemId != request.Through.ItemId)
            throw new InvalidOperationException("Activity changed. Reopen the group to load its current calls.");
        var more = positions.Count > limit;
        if (more) positions.RemoveAt(positions.Count - 1);
        var turns = ReadCompactTurnHeaders(connection, transaction, positions.Select(p => p.TurnId).Distinct().ToArray()).ToDictionary(turn => turn.TurnId);
        var items = ListTranscriptHeaderItemsForTurns(connection, turns.Keys.ToArray(), transaction, positions.Select(p => p.ItemId).ToArray()).ToDictionary(item => item.ItemId);
        var headers = positions.Select(p => turns[p.TurnId] with { Items = [items[p.ItemId]] }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(headers, more, positions.LastOrDefault());
    }

    private static long ReadCompactTranscriptEpoch(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PresentationEpoch FROM AgentSessions WHERE SessionId = $session;";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        return command.ExecuteScalar() is long epoch ? epoch : throw new InvalidOperationException("The session is no longer available.");
    }

    private static IReadOnlyList<AgentTurnRecord> ReadCompactTurnHeaders(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> turnIds)
    {
        if (turnIds.Count == 0) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = AddTurnIdParameters(command, turnIds, "header");
        command.CommandText = $"SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision, TurnSequence FROM AgentTurns WHERE TurnId IN ({string.Join(',', names)});";
        var turns = new List<AgentTurnRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) turns.Add(ReadTurnHeader(reader));
        return turns;
    }

    private static IReadOnlyList<AgentTurnRecord> ReadCompactMessageHeaders(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> turnIds)
        => AttachItems(ReadCompactTurnHeaders(connection, transaction, turnIds), ListTranscriptHeaderItemsForTurns(connection, turnIds, transaction, []));

    internal AgentTranscriptActivityGroup? ReadActivityGroupForTurn(Guid sessionId, Guid turnId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var anchor = ResolveCompactEntry(connection, transaction, sessionId, turnId, null);
        if (anchor is null) return null;
        var epoch = ReadCompactTranscriptEpoch(connection, transaction, sessionId);
        var group = ReadCompactEntries(connection, transaction, sessionId, epoch, "p.TurnSequence = $anchor", anchor.Value.Sequence, 1, false)
            .SingleOrDefault()?.Group;
        if (group is not null) group = group with { Tools = ReadGroupTools(connection, transaction, [group.First.ItemId])[group.First.ItemId] };
        transaction.Commit();
        return group;
    }
}

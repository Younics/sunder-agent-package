using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal HistorySourceSessionPage ListHistorySourceSessionsPage(
        string? afterSessionId,
        int limit,
        long? insertionHighWaterMark = null)
    {
        var boundedLimit = Math.Clamp(limit, 1, HistorySearchLimits.AuthoritativePageSize);
        using var connection = CreateConnection();
        connection.Open();
        var highWaterMark = insertionHighWaterMark ?? GetSessionInsertionHighWaterMark(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.SessionId, s.Title, s.WorkspaceId, COALESCE(w.DisplayName, 'Unknown workspace'),
                    s.ParentSessionId, s.RootSessionId, s.ProfileId, p.DisplayName, s.UpdatedAtUtc
            FROM AgentSessions AS s
            LEFT JOIN AgentWorkspaces AS w ON w.WorkspaceId = s.WorkspaceId
            LEFT JOIN AgentProfiles AS p ON p.ProfileId = s.ProfileId
            WHERE ($afterSessionId IS NULL OR s.SessionId COLLATE BINARY > $afterSessionId COLLATE BINARY)
              AND s.rowid <= $insertionHighWaterMark
            ORDER BY s.SessionId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$afterSessionId", (object?)afterSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$insertionHighWaterMark", highWaterMark);
        command.Parameters.AddWithValue("$limit", boundedLimit + 1);
        using var reader = command.ExecuteReader();
        var sessions = new List<HistorySourceSession>(boundedLimit + 1);
        while (reader.Read())
        {
            sessions.Add(new HistorySourceSession(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
        }

        var hasMore = sessions.Count > boundedLimit;
        if (hasMore)
        {
            sessions.RemoveAt(sessions.Count - 1);
        }
        return new HistorySourceSessionPage(
            sessions,
            hasMore ? sessions[^1].SessionId.ToString("D") : null,
            highWaterMark);
    }

    private static long GetSessionInsertionHighWaterMark(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(rowid), 0) FROM AgentSessions;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal HistorySourceSession? GetHistorySourceSession(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.SessionId, s.Title, s.WorkspaceId, COALESCE(w.DisplayName, 'Unknown workspace'),
                    s.ParentSessionId, s.RootSessionId, s.ProfileId, p.DisplayName, s.UpdatedAtUtc
            FROM AgentSessions AS s
            LEFT JOIN AgentWorkspaces AS w ON w.WorkspaceId = s.WorkspaceId
            LEFT JOIN AgentProfiles AS p ON p.ProfileId = s.ProfileId
            WHERE s.SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new HistorySourceSession(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    internal HistorySessionScope GetHistorySessionScope(Guid sessionId, bool includeChildSessions)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = includeChildSessions
            ? """
                WITH RECURSIVE SessionTree(SessionId) AS (
                    SELECT SessionId FROM AgentSessions WHERE SessionId = $sessionId
                    UNION
                    SELECT child.SessionId
                    FROM AgentSessions AS child
                    INNER JOIN SessionTree AS parent ON child.ParentSessionId = parent.SessionId
                )
                SELECT SessionId FROM SessionTree ORDER BY SessionId COLLATE BINARY;
                """
            : "SELECT SessionId FROM AgentSessions WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        var sessionIds = new List<Guid>();
        while (reader.Read())
        {
            sessionIds.Add(Guid.Parse(reader.GetString(0)));
        }
        return new HistorySessionScope(sessionIds);
    }

    internal HistorySourceSessionMutationPage ListHistorySourceSessionsUpdatedPage(
        DateTimeOffset afterUpdatedAtUtc,
        DateTimeOffset throughUpdatedAtUtc,
        DateTimeOffset? continuationUpdatedAtUtc,
        Guid? continuationSessionId,
        int limit)
    {
        var boundedLimit = Math.Clamp(limit, 1, HistorySearchLimits.AuthoritativePageSize);
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.SessionId, s.Title, s.WorkspaceId, COALESCE(w.DisplayName, 'Unknown workspace'),
                   s.ParentSessionId, s.RootSessionId, s.ProfileId, p.DisplayName, s.UpdatedAtUtc
            FROM AgentSessions AS s
            LEFT JOIN AgentWorkspaces AS w ON w.WorkspaceId = s.WorkspaceId
            LEFT JOIN AgentProfiles AS p ON p.ProfileId = s.ProfileId
            WHERE s.UpdatedAtUtc COLLATE BINARY > $afterUpdatedAtUtc COLLATE BINARY
              AND s.UpdatedAtUtc COLLATE BINARY <= $throughUpdatedAtUtc COLLATE BINARY
              AND ($continuationUpdatedAtUtc IS NULL
                   OR s.UpdatedAtUtc COLLATE BINARY > $continuationUpdatedAtUtc COLLATE BINARY
                   OR (s.UpdatedAtUtc = $continuationUpdatedAtUtc
                       AND s.SessionId COLLATE BINARY > $continuationSessionId COLLATE BINARY))
            ORDER BY s.UpdatedAtUtc COLLATE BINARY, s.SessionId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$afterUpdatedAtUtc", afterUpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$throughUpdatedAtUtc", throughUpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$continuationUpdatedAtUtc",
            continuationUpdatedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$continuationSessionId",
            continuationSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", boundedLimit + 1);
        using var reader = command.ExecuteReader();
        var sessions = new List<HistorySourceSession>(boundedLimit + 1);
        while (reader.Read())
        {
            sessions.Add(new HistorySourceSession(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
        }
        var hasMore = sessions.Count > boundedLimit;
        if (hasMore)
        {
            sessions.RemoveAt(sessions.Count - 1);
        }
        var continuation = hasMore ? sessions[^1] : null;
        return new HistorySourceSessionMutationPage(
            sessions,
            continuation?.UpdatedAtUtc,
            continuation?.SessionId);
    }

    internal HistorySourceTurnPage ListHistorySourceTurnsPage(
        Guid sessionId,
        DateTimeOffset? afterCreatedAtUtc,
        Guid? afterTurnId,
        int limit)
    {
        var boundedLimit = Math.Clamp(limit, 1, HistorySearchLimits.AuthoritativePageSize);
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND ($afterCreatedAtUtc IS NULL
                   OR CreatedAtUtc COLLATE BINARY > $afterCreatedAtUtc COLLATE BINARY
                   OR (CreatedAtUtc = $afterCreatedAtUtc
                       AND TurnId COLLATE BINARY > $afterTurnId COLLATE BINARY))
            ORDER BY CreatedAtUtc COLLATE BINARY, TurnId COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue(
            "$afterCreatedAtUtc",
            afterCreatedAtUtc is null ? DBNull.Value : afterCreatedAtUtc.Value.ToString("O"));
        command.Parameters.AddWithValue(
            "$afterTurnId",
            afterTurnId is null ? DBNull.Value : afterTurnId.Value.ToString());
        command.Parameters.AddWithValue("$limit", boundedLimit + 1);
        var headers = new List<AgentTurnRecord>(boundedLimit + 1);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                headers.Add(ReadTurnHeader(reader));
            }
        }

        var hasMore = headers.Count > boundedLimit;
        if (hasMore)
        {
            headers.RemoveAt(headers.Count - 1);
        }
        var items = ListTurnItemsForTurns(connection, headers.Select(static turn => turn.TurnId).ToArray());
        var turns = AttachItems(headers, items);
        var continuation = hasMore ? turns[^1] : null;
        return new HistorySourceTurnPage(
            turns,
            continuation?.CreatedAtUtc,
            continuation?.TurnId);
    }

    internal HistorySearchValidation? GetHistorySearchValidation(Guid turnId)
        => GetHistorySearchValidations([turnId]).GetValueOrDefault(turnId);

    internal IReadOnlyDictionary<Guid, HistorySearchValidation> GetHistorySearchValidations(
        IReadOnlyCollection<Guid> turnIds)
    {
        if (turnIds.Count == 0)
        {
            return new Dictionary<Guid, HistorySearchValidation>();
        }
        var boundedIds = turnIds.Distinct().Take(HistorySearchLimits.CandidateLimit).ToArray();
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var turnParameters = new string[boundedIds.Length];
        for (var index = 0; index < boundedIds.Length; index++)
        {
            turnParameters[index] = "$turn" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(turnParameters[index], boundedIds[index].ToString());
        }
        command.CommandText = $"""
            SELECT t.TurnId, t.SessionId, s.WorkspaceId, s.RootSessionId, s.ParentSessionId,
                    s.ProfileId, t.Role, t.ContentRevision, t.IsStreaming
            FROM AgentTurns AS t
            INNER JOIN AgentSessions AS s ON s.SessionId = t.SessionId
            WHERE t.TurnId IN ({string.Join(", ", turnParameters)});
            """;
        var builders = new Dictionary<Guid, ValidationBuilder>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var turnId = Guid.Parse(reader.GetString(0));
                builders[turnId] = new ValidationBuilder(
                    turnId,
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Enum.Parse<AgentMessageRole>(reader.GetString(6), ignoreCase: true),
                    reader.GetInt64(7),
                    reader.GetInt64(8) != 0);
            }
        }

        using var itemCommand = connection.CreateCommand();
        itemCommand.Transaction = transaction;
        var itemParameters = new string[builders.Count];
        var builderIndex = 0;
        foreach (var turnId in builders.Keys)
        {
            itemParameters[builderIndex] = "$itemTurn" + builderIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            itemCommand.Parameters.AddWithValue(itemParameters[builderIndex], turnId.ToString());
            builderIndex++;
        }
        if (itemParameters.Length > 0)
        {
            itemCommand.CommandText = $"SELECT TurnId, ItemId, CallId FROM AgentTurnItems WHERE TurnId IN ({string.Join(", ", itemParameters)});";
            using var reader = itemCommand.ExecuteReader();
            while (reader.Read())
            {
                var turnId = Guid.Parse(reader.GetString(0));
                if (!builders.TryGetValue(turnId, out var builder))
                {
                    continue;
                }
                builder.ItemIds.Add(Guid.Parse(reader.GetString(1)));
                if (!reader.IsDBNull(2))
                {
                    builder.CallIds.Add(reader.GetString(2));
                }
            }
        }
        transaction.Commit();
        return builders.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Build());
    }

    internal AgentTranscriptAroundTurnPage LoadTranscriptAroundTurn(
        Guid sessionId,
        Guid turnId,
        int beforeLimit,
        int afterLimit,
        long revision = 0)
    {
        var anchor = GetTranscriptHeader(turnId)
            ?? throw new InvalidOperationException("The transcript anchor is no longer available.");
        if (anchor.SessionId != sessionId)
        {
            throw new InvalidOperationException("The transcript anchor does not belong to the requested session.");
        }

        var boundedBefore = Math.Clamp(beforeLimit, 0, HistorySearchLimits.AroundTurnSideLimit);
        var boundedAfter = Math.Clamp(afterLimit, 0, HistorySearchLimits.AroundTurnSideLimit);
        var before = ListTranscriptHeadersBefore(sessionId, anchor.CreatedAtUtc, anchor.TurnId, boundedBefore + 1);
        var after = ListTranscriptHeadersAfter(sessionId, anchor.CreatedAtUtc, anchor.TurnId, boundedAfter + 1);
        var hasOlder = before.Count > boundedBefore;
        var hasNewer = after.Count > boundedAfter;
        if (hasOlder)
        {
            before = before.Skip(before.Count - boundedBefore).ToArray();
        }
        if (hasNewer)
        {
            after = after.Take(boundedAfter).ToArray();
        }

        return new AgentTranscriptAroundTurnPage(
            revision,
            [.. before, anchor, .. after],
            hasOlder,
            hasNewer,
            anchor.TurnId);
    }

    private sealed class ValidationBuilder(
        Guid turnId,
        Guid sessionId,
        string workspaceId,
        Guid? rootSessionId,
        Guid? parentSessionId,
        string? profileId,
        AgentMessageRole role,
        long contentRevision,
        bool isStreaming)
    {
        internal HashSet<Guid> ItemIds { get; } = [];
        internal HashSet<string> CallIds { get; } = new(StringComparer.Ordinal);

        internal HistorySearchValidation Build()
            => new(
                turnId,
                sessionId,
                workspaceId,
                rootSessionId,
                parentSessionId,
                profileId,
                role,
                contentRevision,
                isStreaming,
                ItemIds,
                CallIds);
    }
}

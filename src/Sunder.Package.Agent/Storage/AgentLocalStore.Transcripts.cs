using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal Action<AgentTranscriptMutationKind>? BeforeFencedTranscriptTransaction { get; set; }

    public AgentTurnRecord AppendTextTurn(Guid sessionId, AgentMessageRole role, string content)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateTextTurn(Guid.NewGuid(), sessionId, role, AgentTurnKind.Message, content, now, now);
        return AppendTurn(
            turn,
            runKey: null,
            expectedEpoch: null,
            mutationKind: AgentTranscriptMutationKind.AssistantText)!;
    }

    internal AgentTurnRecord? TryAppendTextTurn(
        AgentDurableRunKey runKey,
        long expectedEpoch,
        AgentMessageRole role,
        string content)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateTextTurn(
            Guid.NewGuid(),
            runKey.SessionId,
            role,
            AgentTurnKind.Message,
            content,
            now,
            now,
            isStreaming: true);
        return AppendTurn(
            turn,
            runKey,
            expectedEpoch,
            AgentTranscriptMutationKind.AssistantText);
    }

    private AgentTurnRecord? AppendTurn(
        AgentTurnRecord turn,
        AgentDurableRunKey? runKey,
        long? expectedEpoch,
        AgentTranscriptMutationKind mutationKind)
    {
        if (runKey is not null)
        {
            BeforeFencedTranscriptTransaction?.Invoke(mutationKind);
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: runKey is null);
        if (runKey is { } key
            && !CanMutateTranscript(
                connection,
                transaction,
                key,
                expectedEpoch!.Value))
        {
            transaction.Rollback();
            return null;
        }

        InsertTurn(connection, transaction, turn, runKey: runKey);
        TouchSession(connection, turn.SessionId, null, null, transaction);
        transaction.Commit();
        return turn;
    }

    public AgentTurnRecord AppendUserTurn(Guid sessionId, AgentMessageRole role, string content, IReadOnlyList<AgentStoredAttachment> attachments)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateMessageTurn(Guid.NewGuid(), sessionId, role, content, attachments, now, now);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        InsertTurn(connection, transaction, turn);
        TouchSession(connection, sessionId, null, null, transaction);
        transaction.Commit();
        return turn;
    }

    public AgentTranscriptRollbackResult RollbackTranscript(Guid sessionId, Guid anchorTurnId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = RollbackTranscript(connection, transaction, sessionId, anchorTurnId);
        transaction.Commit();
        return result;
    }

    private static AgentTranscriptRollbackResult RollbackTranscript(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid anchorTurnId)
    {
        var anchorTurn = GetRollbackAnchor(connection, transaction, anchorTurnId)
            ?? throw new InvalidOperationException($"Turn '{anchorTurnId}' was not found.");
        if (anchorTurn.SessionId != sessionId)
        {
            throw new InvalidOperationException($"Turn '{anchorTurnId}' does not belong to session '{sessionId}'.");
        }

        if (anchorTurn.Role != AgentMessageRole.User || anchorTurn.Kind != AgentTurnKind.Message)
        {
            throw new InvalidOperationException("Rollback can only start from a user message turn.");
        }

        var deletedTurnIds = ListRollbackTurnIds(connection, transaction, sessionId, anchorTurn);
        if (deletedTurnIds.Count == 0)
        {
            return new AgentTranscriptRollbackResult(sessionId, anchorTurnId, [], []);
        }

        var deletedToolCallIds = ListRollbackToolCallIds(connection, transaction, sessionId, anchorTurn);
        var deletedSessions = ResolveSessionsForParentToolCalls(connection, transaction, sessionId, deletedToolCallIds);
        var deletedSessionIds = deletedSessions.Select(session => session.SessionId).ToArray();

        foreach (var deletedSession in deletedSessions.Reverse())
        {
            DeleteSession(connection, transaction, deletedSession.SessionId.ToString());
        }

        DeleteAffectedPendingPermissionRequests(connection, transaction, sessionId, anchorTurn);
        DeleteAffectedRunCheckpoints(connection, transaction, sessionId, anchorTurn);
        DeleteSessionContinuityState(connection, transaction, sessionId);
        DeleteRollbackTurns(connection, transaction, sessionId, anchorTurn);

        var latestCheckpoint = GetLatestCheckpoint(connection, sessionId, transaction);
        TouchSession(
            connection,
            sessionId,
            latestCheckpoint is null ? AgentSessionState.Active : MapSessionState(latestCheckpoint.Status),
            DateTimeOffset.UtcNow,
            transaction);

        return new AgentTranscriptRollbackResult(sessionId, anchorTurnId, deletedTurnIds, deletedSessionIds);
    }

    public AgentTurnRecord AppendToolCallTurn(
        Guid sessionId,
        AgentMessageRole role,
        string callId,
        string toolId,
        string argumentsJson)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateToolCallTurn(Guid.NewGuid(), sessionId, role, callId, toolId, argumentsJson, now, now);
        return AppendTurn(
            turn,
            runKey: null,
            expectedEpoch: null,
            mutationKind: AgentTranscriptMutationKind.ToolCall)!;
    }

    internal AgentTurnRecord? TryAppendToolCallTurn(
        AgentDurableRunKey runKey,
        long expectedEpoch,
        AgentMessageRole role,
        string callId,
        string toolId,
        string argumentsJson)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateToolCallTurn(
            Guid.NewGuid(),
            runKey.SessionId,
            role,
            callId,
            toolId,
            argumentsJson,
            now,
            now);
        return AppendTurn(
            turn,
            runKey,
            expectedEpoch,
            AgentTranscriptMutationKind.ToolCall);
    }

    public AgentTurnRecord AppendToolResultTurn(
        Guid sessionId,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId,
        string? presentationPayloadJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateToolResultTurn(
            Guid.NewGuid(),
            sessionId,
            callId,
            toolId,
            argumentsJson,
            content,
            resultSummary,
            structuredPayloadJson,
            sourcesJson,
            wasTruncated,
            isError,
            errorCode,
            backendId,
            presentationPayloadJson,
            now,
            now);
        return AppendTurn(
            turn,
            runKey: null,
            expectedEpoch: null,
            mutationKind: AgentTranscriptMutationKind.ToolResult)!;
    }

    internal AgentTurnRecord? TryAppendToolResultTurn(
        AgentDurableRunKey runKey,
        long expectedEpoch,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId,
        string? presentationPayloadJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateToolResultTurn(
            Guid.NewGuid(),
            runKey.SessionId,
            callId,
            toolId,
            argumentsJson,
            content,
            resultSummary,
            structuredPayloadJson,
            sourcesJson,
            wasTruncated,
            isError,
            errorCode,
            backendId,
            presentationPayloadJson,
            now,
            now);
        return AppendTurn(
            turn,
            runKey,
            expectedEpoch,
            AgentTranscriptMutationKind.ToolResult);
    }

    private static bool CanMutateTranscript(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        long expectedEpoch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM AgentRuns
            WHERE RunId = $runId
              AND SessionId = $sessionId
              AND RunRevision = $runRevision
              AND Epoch = $expectedEpoch
              AND Status IN ('Running', 'WaitingForApproval')
              AND FinishedAtUtc IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM AgentRuns newer
                  WHERE newer.SessionId = $sessionId
                    AND newer.RunRevision > $runRevision)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
        command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
        return command.ExecuteScalar() is not null;
    }

    private static AgentTurnRecord? GetRollbackAnchor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid turnId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming FROM AgentTurns WHERE TurnId = $turnId;";
        command.Parameters.AddWithValue("$turnId", turnId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentTurnRecord(
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
            }
            : null;
    }

    private static bool CanUpdateProjectedMessage(AgentTurnRecord turn)
        => turn.Kind == AgentTurnKind.Message
           && turn.Items.Count == 1
           && turn.Items[0].Kind == AgentTurnItemKind.Text;

    private static IReadOnlyList<Guid> ListRollbackTurnIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        AgentTurnRecord anchorTurn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildRollbackTurnsQuery("SELECT TurnId");
        AddRollbackTurnParameters(command, sessionId, anchorTurn);

        using var reader = command.ExecuteReader();
        var turnIds = new List<Guid>();
        while (reader.Read())
        {
            turnIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return turnIds;
    }

    private static IReadOnlySet<string> ListRollbackToolCallIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        AgentTurnRecord anchorTurn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT i.CallId
            FROM AgentTurnItems i
            INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
            WHERE t.SessionId = $sessionId
              AND (t.CreatedAtUtc > $anchorCreatedAtUtc OR (t.CreatedAtUtc = $anchorCreatedAtUtc AND t.TurnId >= $anchorTurnId))
              AND i.Kind = $toolCallKind
              AND i.CallId IS NOT NULL
              AND i.CallId <> '';
            """;
        AddRollbackTurnParameters(command, sessionId, anchorTurn);
        command.Parameters.AddWithValue("$toolCallKind", AgentTurnItemKind.ToolCall.ToString());

        using var reader = command.ExecuteReader();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            callIds.Add(reader.GetString(0));
        }

        return callIds;
    }

    private static IReadOnlyList<AgentSessionRecord> ResolveSessionsForParentToolCalls(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid parentSessionId,
        IReadOnlySet<string> toolCallIds)
    {
        if (toolCallIds.Count == 0)
        {
            return [];
        }

        var sessions = ListSessions(connection, transaction);
        var descendantsByParent = sessions
            .Where(session => session.ParentSessionId is not null)
            .GroupBy(session => session.ParentSessionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var roots = sessions
            .Where(session => session.ParentSessionId == parentSessionId
                              && !string.IsNullOrWhiteSpace(session.ParentToolCallId)
                              && toolCallIds.Contains(session.ParentToolCallId))
            .OrderBy(session => session.CreatedAtUtc)
            .ToArray();

        var ordered = new List<AgentSessionRecord>();
        var visited = new HashSet<Guid>();
        var queue = new Queue<AgentSessionRecord>(roots);
        while (queue.Count > 0)
        {
            var session = queue.Dequeue();
            if (!visited.Add(session.SessionId))
            {
                continue;
            }

            ordered.Add(session);
            if (!descendantsByParent.TryGetValue(session.SessionId, out var children))
            {
                continue;
            }

            foreach (var child in children.OrderBy(child => child.CreatedAtUtc))
            {
                queue.Enqueue(child);
            }
        }

        return ordered;
    }

    private static void DeleteAffectedPendingPermissionRequests(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        AgentTurnRecord anchorTurn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM AgentPendingPermissionRequests
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc >= $anchorCreatedAtUtc
                   OR UserTurnId IN (
                       SELECT TurnId
                       FROM AgentTurns
                       WHERE SessionId = $sessionId
                         AND (CreatedAtUtc > $anchorCreatedAtUtc OR (CreatedAtUtc = $anchorCreatedAtUtc AND TurnId >= $anchorTurnId))
                   ));
            """;
        AddRollbackTurnParameters(command, sessionId, anchorTurn);
        command.ExecuteNonQuery();
    }

    private static void DeleteAffectedRunCheckpoints(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        AgentTurnRecord anchorTurn)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM AgentRunCheckpoints WHERE SessionId = $sessionId AND CreatedAtUtc >= $anchorCreatedAtUtc;";
        AddRollbackTurnParameters(command, sessionId, anchorTurn);
        command.ExecuteNonQuery();
    }

    private static void DeleteSessionContinuityState(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var deleteContextCheckpoints = connection.CreateCommand();
        deleteContextCheckpoints.Transaction = transaction;
        deleteContextCheckpoints.CommandText = "DELETE FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId;";
        deleteContextCheckpoints.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        deleteContextCheckpoints.ExecuteNonQuery();
    }

    private static void DeleteRollbackTurns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        AgentTurnRecord anchorTurn)
    {
        using var deleteTurnItems = connection.CreateCommand();
        deleteTurnItems.Transaction = transaction;
        deleteTurnItems.CommandText = """
            DELETE FROM AgentTurnItems
            WHERE TurnId IN (
                SELECT TurnId
                FROM AgentTurns
                WHERE SessionId = $sessionId
                  AND (CreatedAtUtc > $anchorCreatedAtUtc OR (CreatedAtUtc = $anchorCreatedAtUtc AND TurnId >= $anchorTurnId))
            );
            """;
        AddRollbackTurnParameters(deleteTurnItems, sessionId, anchorTurn);
        deleteTurnItems.ExecuteNonQuery();

        using var deleteTurns = connection.CreateCommand();
        deleteTurns.Transaction = transaction;
        deleteTurns.CommandText = """
            DELETE FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc > $anchorCreatedAtUtc OR (CreatedAtUtc = $anchorCreatedAtUtc AND TurnId >= $anchorTurnId));
            """;
        AddRollbackTurnParameters(deleteTurns, sessionId, anchorTurn);
        deleteTurns.ExecuteNonQuery();
    }

    private static string BuildRollbackTurnsQuery(string selectOrDelete)
        => $"""
            {selectOrDelete}
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (CreatedAtUtc > $anchorCreatedAtUtc OR (CreatedAtUtc = $anchorCreatedAtUtc AND TurnId >= $anchorTurnId))
            ORDER BY CreatedAtUtc, TurnId;
            """;

    private static void AddRollbackTurnParameters(SqliteCommand command, Guid sessionId, AgentTurnRecord anchorTurn)
    {
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$anchorCreatedAtUtc", anchorTurn.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$anchorTurnId", anchorTurn.TurnId.ToString());
    }

    private static void InsertTurn(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AgentTurnRecord turn,
        bool ignoreConflicts = false,
        AgentDurableRunKey? runKey = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT {(ignoreConflicts ? "OR IGNORE " : string.Empty)}INTO AgentTurns (TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision) VALUES ($id, $sessionId, $role, $kind, $created, $updated, $contentRevision, $isStreaming, $runId, $runRevision);";
        command.Parameters.AddWithValue("$id", turn.TurnId.ToString());
        command.Parameters.AddWithValue("$sessionId", turn.SessionId.ToString());
        command.Parameters.AddWithValue("$role", turn.Role.ToString());
        command.Parameters.AddWithValue("$kind", turn.Kind.ToString());
        command.Parameters.AddWithValue("$created", turn.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", turn.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$contentRevision", turn.ContentRevision);
        command.Parameters.AddWithValue("$isStreaming", turn.IsStreaming ? 1 : 0);
        command.Parameters.AddWithValue("$runId", runKey is { } key ? key.RunId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$runRevision", runKey is { } ownedKey ? ownedKey.RunRevision : DBNull.Value);
        command.ExecuteNonQuery();

        foreach (var item in turn.Items.OrderBy(item => item.SequenceNumber))
        {
            InsertTurnItem(connection, transaction, item, ignoreConflicts);
        }
    }

    private static void InsertTurnItem(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AgentTurnItemRecord item,
        bool ignoreConflicts)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT {(ignoreConflicts ? "OR IGNORE " : string.Empty)}INTO AgentTurnItems (ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId, ArgumentsJson, ResultSummary, StructuredPayloadJson, SourcesJson, WasTruncated, IsError, ErrorCode, BackendId, PresentationPayloadJson) VALUES ($itemId, $turnId, $sequenceNumber, $kind, $textContent, $callId, $toolId, $argumentsJson, $resultSummary, $structuredPayloadJson, $sourcesJson, $wasTruncated, $isError, $errorCode, $backendId, $presentationPayloadJson);";
        command.Parameters.AddWithValue("$itemId", item.ItemId.ToString());
        command.Parameters.AddWithValue("$turnId", item.TurnId.ToString());
        command.Parameters.AddWithValue("$sequenceNumber", item.SequenceNumber);
        command.Parameters.AddWithValue("$kind", item.Kind.ToString());
        command.Parameters.AddWithValue("$textContent", (object?)item.TextContent ?? DBNull.Value);
        command.Parameters.AddWithValue("$callId", (object?)item.CallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolId", (object?)item.ToolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", (object?)item.ArgumentsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultSummary", (object?)item.ResultSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$structuredPayloadJson", (object?)item.StructuredPayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourcesJson", (object?)item.SourcesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$wasTruncated", item.WasTruncated ? 1 : 0);
        command.Parameters.AddWithValue("$isError", item.IsError ? 1 : 0);
        command.Parameters.AddWithValue("$errorCode", (object?)item.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$backendId", (object?)item.BackendId ?? DBNull.Value);
        command.Parameters.AddWithValue("$presentationPayloadJson", (object?)item.PresentationPayloadJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

}

internal enum AgentTranscriptMutationKind
{
    AssistantText = 0,
    ToolCall = 1,
    ToolResult = 2,
    UserRunStart = 3,
}

internal sealed record AgentTurnWriteResult(
    AgentTurnRecord Turn,
    AgentTurnMutationKind MutationKind,
    int BaseContentLength,
    string? Text);

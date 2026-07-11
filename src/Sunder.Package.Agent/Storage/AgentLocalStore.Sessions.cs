using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    public IReadOnlyList<AgentSessionRecord> ListSessions()
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListSessions(connection);
    }

    public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListSessionsForWorkspace(connection, workspaceId);
    }

    public AgentSessionRecord CreateSession(
        string title,
        Guid? parentSessionId = null,
        Guid? rootSessionId = null,
        Guid? parentRunId = null,
        long? parentRunRevision = null,
        string? parentToolCallId = null,
        string? taskId = null,
        string? profileId = null,
        string? behaviorLoopId = null,
        string? agentKind = null,
        string? workspaceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var resolvedRootSessionId = rootSessionId ?? (parentSessionId is null ? sessionId : parentSessionId.Value);
        var resolvedWorkspaceId = RequireSessionWorkspaceIdForWrite(workspaceId);
        var session = new AgentSessionRecord(
            sessionId,
            title,
            AgentSessionState.Active,
            now,
            now,
            parentSessionId,
            resolvedRootSessionId,
            parentRunId,
            parentRunRevision,
            string.IsNullOrWhiteSpace(parentToolCallId) ? null : parentToolCallId.Trim(),
            string.IsNullOrWhiteSpace(taskId) ? null : taskId.Trim(),
            string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
            string.IsNullOrWhiteSpace(behaviorLoopId) ? null : behaviorLoopId.Trim(),
            string.IsNullOrWhiteSpace(agentKind) ? "agent" : agentKind.Trim(),
            resolvedWorkspaceId);

        using var connection = CreateConnection();
        connection.Open();
        if (string.Equals(resolvedWorkspaceId, UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            EnsureUnassignedSessionsWorkspace(connection);
        }

        InsertSession(connection, session);
        return session;
    }

    public void UpdateSession(AgentSessionRecord session)
    {
        using var connection = CreateConnection();
        connection.Open();
        var workspaceId = RequireSessionWorkspaceIdForWrite(session.WorkspaceId);
        if (string.Equals(workspaceId, UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            EnsureUnassignedSessionsWorkspace(connection);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AgentSessions SET Title = $title, State = $state, UpdatedAtUtc = $updated, WorkspaceId = $workspaceId, ParentSessionId = $parentSessionId, RootSessionId = $rootSessionId, ParentRunId = $parentRunId, ParentRunRevision = $parentRunRevision, ParentToolCallId = $parentToolCallId, TaskId = $taskId, ProfileId = $profileId, BehaviorLoopId = $behaviorLoopId, AgentKind = $agentKind WHERE SessionId = $id;";
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$state", session.State.ToString());
        command.Parameters.AddWithValue("$updated", session.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$parentSessionId", session.ParentSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rootSessionId", session.RootSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", session.ParentRunId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentRunRevision", session.ParentRunRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentToolCallId", (object?)session.ParentToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)session.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileId", (object?)session.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$behaviorLoopId", (object?)session.BehaviorLoopId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentKind", (object?)session.AgentKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", session.SessionId.ToString());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Guid> DeleteSessionTree(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var sessions = ResolveSessionTree(connection, transaction, sessionId);
        foreach (var session in sessions.Reverse())
        {
            DeleteSession(connection, transaction, session.SessionId.ToString());
        }

        transaction.Commit();
        return sessions.Select(session => session.SessionId).ToArray();
    }

    public IReadOnlyList<Guid> DeleteSessionTreesForWorkspace(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var deletedSessionIds = DeleteSessionTreesForWorkspace(connection, transaction, workspaceId);

        transaction.Commit();
        return deletedSessionIds;
    }

    public AgentSessionRecord? GetSession(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId, Title, State, CreatedAtUtc, UpdatedAtUtc, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, WorkspaceId FROM AgentSessions WHERE SessionId = $id;";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        using var reader = command.ExecuteReader();

        return reader.Read()
            ? new AgentSessionRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Enum.Parse<AgentSessionState>(reader.GetString(2), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14))
             : null;
    }

    public AgentRunCheckpointRecord SaveCheckpoint(Guid sessionId, long runRevision, AgentRunStatus status, string? summary)
    {
        var checkpoint = new AgentRunCheckpointRecord(Guid.NewGuid(), sessionId, runRevision, status, summary, DateTimeOffset.UtcNow);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        InsertCheckpoint(connection, transaction, checkpoint);
        if (HasDurableRun(connection, transaction, sessionId, runRevision)
            && !ProjectCheckpointToRun(connection, transaction, checkpoint))
        {
            transaction.Rollback();
            throw new InvalidOperationException(
                $"Run '{sessionId}:{runRevision}' rejected the illegal or stale transition to '{status}'.");
        }

        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        transaction.Commit();
        return checkpoint;
    }

    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetLatestCheckpoint(connection, sessionId, null);
    }

    internal AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId, long runRevision)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc
            FROM AgentRunCheckpoints
            WHERE SessionId = $sessionId AND RunRevision = $runRevision
            ORDER BY CreatedAtUtc DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", runRevision);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentRunCheckpointRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                Enum.Parse<AgentRunStatus>(reader.GetString(3), ignoreCase: true),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)))
            : null;
    }

    private static AgentRunCheckpointRecord? GetLatestCheckpoint(
        SqliteConnection connection,
        Guid sessionId,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc FROM AgentRunCheckpoints WHERE SessionId = $sessionId ORDER BY CreatedAtUtc DESC, rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentRunCheckpointRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                Enum.Parse<AgentRunStatus>(reader.GetString(3), ignoreCase: true),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)))
            : null;
    }

    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId, SummaryText, UpdatedAtUtc FROM AgentWorkingSummaries WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentWorkingSummaryRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)))
            : null;
    }

    public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ContextCheckpointId, SessionId, FirstOmittedTurnId, LastOmittedTurnId, OmittedTurnCount, SummaryText, DetailsJson, CreatedAtUtc FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId ORDER BY CreatedAtUtc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadSessionContextCheckpoint(reader)
            : null;
    }

    public AgentSessionContextCheckpointRecord SaveSessionContextCheckpoint(
        Guid sessionId,
        Guid? firstOmittedTurnId,
        Guid? lastOmittedTurnId,
        int omittedTurnCount,
        string summaryText,
        string? detailsJson)
    {
        var checkpoint = new AgentSessionContextCheckpointRecord(
            Guid.NewGuid(),
            sessionId,
            firstOmittedTurnId,
            lastOmittedTurnId,
            omittedTurnCount,
            summaryText.Trim(),
            string.IsNullOrWhiteSpace(detailsJson) ? null : detailsJson,
            DateTimeOffset.UtcNow);

        using var connection = CreateConnection();
        connection.Open();
        InsertSessionContextCheckpoint(connection, checkpoint);
        TouchSession(connection, sessionId, null, checkpoint.CreatedAtUtc, transaction: null);
        return checkpoint;
    }

    public AgentWorkingSummaryRecord? SaveWorkingSummary(Guid sessionId, string? summaryText)
    {
        using var connection = CreateConnection();
        connection.Open();

        if (string.IsNullOrWhiteSpace(summaryText))
        {
            using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM AgentWorkingSummaries WHERE SessionId = $sessionId;";
            deleteCommand.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            deleteCommand.ExecuteNonQuery();
            return null;
        }

        var record = new AgentWorkingSummaryRecord(sessionId, summaryText.Trim(), DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentWorkingSummaries (SessionId, SummaryText, UpdatedAtUtc)
            VALUES ($sessionId, $summaryText, $updatedAtUtc)
            ON CONFLICT(SessionId) DO UPDATE SET
                SummaryText = excluded.SummaryText,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$sessionId", record.SessionId.ToString());
        command.Parameters.AddWithValue("$summaryText", record.SummaryText);
        command.Parameters.AddWithValue("$updatedAtUtc", record.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
        return record;
    }

    public long GetNextRunRevision(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetNextRunRevision(connection, transaction: null, sessionId);
    }

    private static IReadOnlyList<AgentSessionRecord> ListSessions(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SessionId, Title, State, CreatedAtUtc, UpdatedAtUtc, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, WorkspaceId FROM AgentSessions ORDER BY UpdatedAtUtc DESC;";

        return ReadSessions(command);
    }

    private static IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(SqliteConnection connection, string workspaceId)
    {
        var normalizedWorkspaceId = NormalizeWorkspaceId(workspaceId);
        if (normalizedWorkspaceId is null)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId, Title, State, CreatedAtUtc, UpdatedAtUtc, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, WorkspaceId FROM AgentSessions WHERE WorkspaceId = $workspaceId ORDER BY UpdatedAtUtc DESC;";
        command.Parameters.AddWithValue("$workspaceId", normalizedWorkspaceId);

        return ReadSessions(command);
    }

    private static IReadOnlyList<AgentSessionRecord> ReadSessions(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var items = new List<AgentSessionRecord>();
        while (reader.Read())
        {
            items.Add(new AgentSessionRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Enum.Parse<AgentSessionState>(reader.GetString(2), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)
            ));
        }

        return items;
    }

    private static IReadOnlyList<AgentRunCheckpointRecord> ListRecentCheckpoints(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc FROM AgentRunCheckpoints ORDER BY CreatedAtUtc DESC LIMIT 5;";

        using var reader = command.ExecuteReader();
        var items = new List<AgentRunCheckpointRecord>();
        while (reader.Read())
        {
            items.Add(new AgentRunCheckpointRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                Enum.Parse<AgentRunStatus>(reader.GetString(3), ignoreCase: true),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))
            ));
        }

        return items;
    }

    private static void InsertSession(SqliteConnection connection, AgentSessionRecord session)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AgentSessions (SessionId, Title, State, WorkspaceId, ParentSessionId, RootSessionId, ParentRunId, ParentRunRevision, ParentToolCallId, TaskId, ProfileId, BehaviorLoopId, AgentKind, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $title, $state, $workspaceId, $parentSessionId, $rootSessionId, $parentRunId, $parentRunRevision, $parentToolCallId, $taskId, $profileId, $behaviorLoopId, $agentKind, $created, $updated);";
        command.Parameters.AddWithValue("$id", session.SessionId.ToString());
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$state", session.State.ToString());
        command.Parameters.AddWithValue("$workspaceId", RequireSessionWorkspaceIdForWrite(session.WorkspaceId));
        command.Parameters.AddWithValue("$parentSessionId", session.ParentSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rootSessionId", session.RootSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", session.ParentRunId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentRunRevision", session.ParentRunRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$parentToolCallId", (object?)session.ParentToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)session.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileId", (object?)session.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$behaviorLoopId", (object?)session.BehaviorLoopId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentKind", (object?)session.AgentKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", session.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", session.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string? NormalizeWorkspaceId(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();

    private static string RequireSessionWorkspaceIdForWrite(string? workspaceId)
        => NormalizeWorkspaceId(workspaceId)
           ?? throw new InvalidOperationException("Sessions must be created with an assigned workspace.");

    private static IReadOnlyList<Guid> DeleteSessionTreesForWorkspace(SqliteConnection connection, SqliteTransaction transaction, string workspaceId)
    {
        var normalizedWorkspaceId = NormalizeWorkspaceId(workspaceId);
        if (normalizedWorkspaceId is null)
        {
            return [];
        }

        var candidateSessionIds = ListSessions(connection, transaction)
            .Where(session => string.Equals(session.WorkspaceId, normalizedWorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Select(session => session.SessionId)
            .ToArray();
        var deletedSessionIds = new List<Guid>();
        var deletedSessionIdSet = new HashSet<Guid>();
        foreach (var sessionId in candidateSessionIds)
        {
            if (deletedSessionIdSet.Contains(sessionId))
            {
                continue;
            }

            var sessions = ResolveSessionTree(connection, transaction, sessionId);
            foreach (var session in sessions.Reverse())
            {
                DeleteSession(connection, transaction, session.SessionId.ToString());
            }

            foreach (var session in sessions)
            {
                if (deletedSessionIdSet.Add(session.SessionId))
                {
                    deletedSessionIds.Add(session.SessionId);
                }
            }
        }

        return deletedSessionIds;
    }

    private static IReadOnlyList<AgentSessionRecord> ResolveSessionTree(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        var sessions = ListSessions(connection, transaction);
        var rootSession = sessions.FirstOrDefault(session => session.SessionId == sessionId);
        if (rootSession is null)
        {
            return [];
        }

        var descendantsByParent = sessions
            .Where(session => session.ParentSessionId is not null)
            .GroupBy(session => session.ParentSessionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var ordered = new List<AgentSessionRecord> { rootSession };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootSession.SessionId);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!descendantsByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                ordered.Add(child);
                queue.Enqueue(child.SessionId);
            }
        }

        return ordered;
    }

    private static void DeleteSession(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        using var deleteTurnItems = connection.CreateCommand();
        deleteTurnItems.Transaction = transaction;
        deleteTurnItems.CommandText = "DELETE FROM AgentTurnItems WHERE TurnId IN (SELECT TurnId FROM AgentTurns WHERE SessionId = $sessionId);";
        deleteTurnItems.Parameters.AddWithValue("$sessionId", sessionId);
        deleteTurnItems.ExecuteNonQuery();

        using var deleteTurns = connection.CreateCommand();
        deleteTurns.Transaction = transaction;
        deleteTurns.CommandText = "DELETE FROM AgentTurns WHERE SessionId = $sessionId;";
        deleteTurns.Parameters.AddWithValue("$sessionId", sessionId);
        deleteTurns.ExecuteNonQuery();

        using var deleteCheckpoints = connection.CreateCommand();
        deleteCheckpoints.Transaction = transaction;
        deleteCheckpoints.CommandText = "DELETE FROM AgentRunCheckpoints WHERE SessionId = $sessionId;";
        deleteCheckpoints.Parameters.AddWithValue("$sessionId", sessionId);
        deleteCheckpoints.ExecuteNonQuery();

        using var deleteRuns = connection.CreateCommand();
        deleteRuns.Transaction = transaction;
        deleteRuns.CommandText = "DELETE FROM AgentRuns WHERE SessionId = $sessionId;";
        deleteRuns.Parameters.AddWithValue("$sessionId", sessionId);
        deleteRuns.ExecuteNonQuery();

        using var deleteParentContinuationWork = connection.CreateCommand();
        deleteParentContinuationWork.Transaction = transaction;
        deleteParentContinuationWork.CommandText = "DELETE FROM AgentParentContinuationWork WHERE ParentSessionId = $sessionId OR ChildSessionId = $sessionId;";
        deleteParentContinuationWork.Parameters.AddWithValue("$sessionId", sessionId);
        deleteParentContinuationWork.ExecuteNonQuery();

        using var deleteWorkingSummaries = connection.CreateCommand();
        deleteWorkingSummaries.Transaction = transaction;
        deleteWorkingSummaries.CommandText = "DELETE FROM AgentWorkingSummaries WHERE SessionId = $sessionId;";
        deleteWorkingSummaries.Parameters.AddWithValue("$sessionId", sessionId);
        deleteWorkingSummaries.ExecuteNonQuery();

        using var deleteSessionContextCheckpoints = connection.CreateCommand();
        deleteSessionContextCheckpoints.Transaction = transaction;
        deleteSessionContextCheckpoints.CommandText = "DELETE FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId;";
        deleteSessionContextCheckpoints.Parameters.AddWithValue("$sessionId", sessionId);
        deleteSessionContextCheckpoints.ExecuteNonQuery();

        using var deletePermissionStates = connection.CreateCommand();
        deletePermissionStates.Transaction = transaction;
        deletePermissionStates.CommandText = "DELETE FROM AgentSessionPermissionStates WHERE SessionId = $sessionId;";
        deletePermissionStates.Parameters.AddWithValue("$sessionId", sessionId);
        deletePermissionStates.ExecuteNonQuery();

        using var deletePermissionApprovals = connection.CreateCommand();
        deletePermissionApprovals.Transaction = transaction;
        deletePermissionApprovals.CommandText = "DELETE FROM AgentSessionPermissionApprovals WHERE SessionId = $sessionId;";
        deletePermissionApprovals.Parameters.AddWithValue("$sessionId", sessionId);
        deletePermissionApprovals.ExecuteNonQuery();

        using var deletePendingPermissionRequests = connection.CreateCommand();
        deletePendingPermissionRequests.Transaction = transaction;
        deletePendingPermissionRequests.CommandText = "DELETE FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId OR ParentSessionId = $sessionId OR RootSessionId = $sessionId;";
        deletePendingPermissionRequests.Parameters.AddWithValue("$sessionId", sessionId);
        deletePendingPermissionRequests.ExecuteNonQuery();

        using var deleteSession = connection.CreateCommand();
        deleteSession.Transaction = transaction;
        deleteSession.CommandText = "DELETE FROM AgentSessions WHERE SessionId = $sessionId;";
        deleteSession.Parameters.AddWithValue("$sessionId", sessionId);
        deleteSession.ExecuteNonQuery();
    }

    private static void InsertCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunCheckpointRecord checkpoint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO AgentRunCheckpoints (CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc) VALUES ($id, $sessionId, $revision, $status, $summary, $created);";
        command.Parameters.AddWithValue("$id", checkpoint.CheckpointId.ToString());
        command.Parameters.AddWithValue("$sessionId", checkpoint.SessionId.ToString());
        command.Parameters.AddWithValue("$revision", checkpoint.RunRevision);
        command.Parameters.AddWithValue("$status", checkpoint.Status.ToString());
        command.Parameters.AddWithValue("$summary", (object?)checkpoint.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", checkpoint.CreatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static bool ProjectCheckpointToRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunCheckpointRecord checkpoint)
    {
        var isFinished = IsFinishedRunStatus(checkpoint.Status);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentRuns
            SET Epoch = Epoch + 1,
                Status = $status,
                UpdatedAtUtc = $updatedAtUtc,
                FinishedAtUtc = $finishedAtUtc,
                SuspensionKind = CASE WHEN $isFinished = 1 THEN NULL ELSE SuspensionKind END,
                ContinuationToken = CASE WHEN $isFinished = 1 THEN NULL ELSE ContinuationToken END,
                SuspensionDataJson = CASE WHEN $isFinished = 1 THEN NULL ELSE SuspensionDataJson END
            WHERE SessionId = $sessionId
              AND RunRevision = $runRevision
              AND FinishedAtUtc IS NULL
              AND (
                  (Status IN ('Preparing', 'Idle') AND $status IN ('Running', 'Interrupted', 'Stopped', 'Failed'))
                  OR (Status = 'Running' AND $status IN ('Running', 'Interrupted', 'Stopped', 'Completed', 'Failed'))
                  OR (Status = 'WaitingForApproval' AND $status IN ('Interrupted', 'Stopped', 'Failed'))
              );
            """;
        command.Parameters.AddWithValue("$status", checkpoint.Status.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", checkpoint.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$finishedAtUtc",
            isFinished ? checkpoint.CreatedAtUtc.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$isFinished", isFinished ? 1 : 0);
        command.Parameters.AddWithValue("$sessionId", checkpoint.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", checkpoint.RunRevision);
        return command.ExecuteNonQuery() == 1;
    }

    private static bool HasDurableRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        long runRevision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM AgentRuns WHERE SessionId = $sessionId AND RunRevision = $runRevision LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", runRevision);
        return command.ExecuteScalar() is not null;
    }

    private static void TouchSessionForCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunCheckpointRecord checkpoint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentSessions
            SET State = CASE
                    WHEN $runRevision >= (
                        SELECT COALESCE(MAX(RunRevision), $runRevision)
                        FROM (
                            SELECT RunRevision FROM AgentRuns WHERE SessionId = $sessionId
                            UNION ALL
                            SELECT RunRevision FROM AgentRunCheckpoints WHERE SessionId = $sessionId
                        )
                    ) THEN $state
                    ELSE State
                END,
                UpdatedAtUtc = $updatedAtUtc
            WHERE SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$runRevision", checkpoint.RunRevision);
        command.Parameters.AddWithValue("$state", MapSessionState(checkpoint.Status).ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", checkpoint.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", checkpoint.SessionId.ToString());
        command.ExecuteNonQuery();
    }

    private static bool IsFinishedRunStatus(AgentRunStatus status)
        => status is AgentRunStatus.Interrupted
            or AgentRunStatus.Stopped
            or AgentRunStatus.Completed
            or AgentRunStatus.Failed;

    private static void InsertSessionContextCheckpoint(SqliteConnection connection, AgentSessionContextCheckpointRecord checkpoint)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AgentSessionContextCheckpoints (ContextCheckpointId, SessionId, FirstOmittedTurnId, LastOmittedTurnId, OmittedTurnCount, SummaryText, DetailsJson, CreatedAtUtc) VALUES ($id, $sessionId, $firstOmittedTurnId, $lastOmittedTurnId, $omittedTurnCount, $summaryText, $detailsJson, $created);";
        command.Parameters.AddWithValue("$id", checkpoint.ContextCheckpointId.ToString());
        command.Parameters.AddWithValue("$sessionId", checkpoint.SessionId.ToString());
        command.Parameters.AddWithValue("$firstOmittedTurnId", checkpoint.FirstOmittedTurnId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastOmittedTurnId", checkpoint.LastOmittedTurnId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$omittedTurnCount", checkpoint.OmittedTurnCount);
        command.Parameters.AddWithValue("$summaryText", checkpoint.SummaryText);
        command.Parameters.AddWithValue("$detailsJson", (object?)checkpoint.DetailsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", checkpoint.CreatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static AgentSessionContextCheckpointRecord ReadSessionContextCheckpoint(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            Convert.ToInt32(reader.GetInt64(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)));

    private static void TouchSession(
        SqliteConnection connection,
        Guid sessionId,
        AgentSessionState? state,
        DateTimeOffset? updatedAtUtc,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = state is null
            ? "UPDATE AgentSessions SET UpdatedAtUtc = $updated WHERE SessionId = $id;"
            : "UPDATE AgentSessions SET State = $state, UpdatedAtUtc = $updated WHERE SessionId = $id;";
        command.Parameters.AddWithValue("$updated", (updatedAtUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        if (state is not null)
        {
            command.Parameters.AddWithValue("$state", state.Value.ToString());
        }
        command.ExecuteNonQuery();
    }

    private static AgentSessionState MapSessionState(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Interrupted => AgentSessionState.Interrupted,
            AgentRunStatus.Stopped => AgentSessionState.Stopped,
            AgentRunStatus.Completed => AgentSessionState.Completed,
            AgentRunStatus.Failed => AgentSessionState.Failed,
            AgentRunStatus.WaitingForApproval => AgentSessionState.Active,
            _ => AgentSessionState.Active,
        };
}

using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const long InitialRunEpoch = 1;

    internal AgentDurableRunRecord ReserveRun(
        Guid sessionId,
        string profileId,
        string userMessage)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        EnsureSessionExists(connection, transaction, sessionId);
        var runRevision = GetNextRunRevision(connection, transaction, sessionId);
        var now = DateTimeOffset.UtcNow;
        var run = new AgentDurableRunRecord(
            new AgentDurableRunKey(Guid.NewGuid(), sessionId, runRevision),
            InitialRunEpoch,
            AgentDurableRunStatus.Preparing,
            profileId?.Trim() ?? string.Empty,
            userMessage ?? string.Empty,
            now,
            now,
            FinishedAtUtc: null);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentRuns (
                RunId,
                SessionId,
                RunRevision,
                Epoch,
                Status,
                ProfileId,
                UserMessage,
                StartedAtUtc,
                UpdatedAtUtc,
                FinishedAtUtc)
            VALUES (
                $runId,
                $sessionId,
                $runRevision,
                $epoch,
                $status,
                $profileId,
                $userMessage,
                $startedAtUtc,
                $updatedAtUtc,
                NULL);
            """;
        command.Parameters.AddWithValue("$runId", run.Key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", run.Key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", run.Key.RunRevision);
        command.Parameters.AddWithValue("$epoch", run.Epoch);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$profileId", run.ProfileId);
        command.Parameters.AddWithValue("$userMessage", run.UserMessage);
        command.Parameters.AddWithValue("$startedAtUtc", run.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", run.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();

        transaction.Commit();
        return run;
    }

    internal AgentDurableRunRecord? GetRun(Guid runId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, SessionId, RunRevision, Epoch, Status, ProfileId, UserMessage,
                   StartedAtUtc, UpdatedAtUtc, FinishedAtUtc, SuspensionKind,
                   ContinuationToken, SuspensionDataJson
            FROM AgentRuns
            WHERE RunId = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    internal AgentDurableRunRecord? GetLatestRun(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, SessionId, RunRevision, Epoch, Status, ProfileId, UserMessage,
                   StartedAtUtc, UpdatedAtUtc, FinishedAtUtc, SuspensionKind,
                   ContinuationToken, SuspensionDataJson
            FROM AgentRuns
            WHERE SessionId = $sessionId
            ORDER BY RunRevision DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private void RecoverUnownedActiveRuns()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;
        const string summary =
            "The prior process ended while this run was active; the run was interrupted during startup recovery.";
        var recovered = new List<AgentDurableRunKey>();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT RunId, SessionId, RunRevision
                FROM AgentRuns
                WHERE Status IN ('Preparing', 'Running')
                  AND FinishedAtUtc IS NULL
                  AND SuspensionKind IS NULL
                  AND ContinuationToken IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentPendingPermissionRequests permission
                      WHERE permission.RunId = AgentRuns.RunId
                        AND permission.SessionId = AgentRuns.SessionId
                        AND permission.RunRevision = AgentRuns.RunRevision
                        AND permission.Status IN ('Pending', 'Claimed'))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentParentContinuationWork work
                      WHERE work.ParentRunId = AgentRuns.RunId
                        AND work.ParentSessionId = AgentRuns.SessionId
                        AND work.ParentRunRevision = AgentRuns.RunRevision
                        AND work.Status = 'Dispatching'
                        AND work.ExecutionStartedAtUtc IS NULL);
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                recovered.Add(new AgentDurableRunKey(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt64(2)));
            }
        }

        foreach (var key in recovered)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = 'Interrupted',
                    UpdatedAtUtc = $updatedAtUtc,
                    FinishedAtUtc = $updatedAtUtc
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Status IN ('Preparing', 'Running')
                  AND FinishedAtUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                continue;
            }

            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                key.SessionId,
                key.RunRevision,
                AgentRunStatus.Interrupted,
                summary,
                now);
            InsertCheckpoint(connection, transaction, checkpoint);
            TouchSessionForCheckpoint(connection, transaction, checkpoint);
        }

        transaction.Commit();
    }

    internal AgentRunTransitionResult? TryTransitionRun(
        AgentDurableRunKey key,
        long expectedEpoch,
        AgentRunStatus status,
        string? summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;
        var isTerminal = IsFinishedRunStatus(status);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = $status,
                    UpdatedAtUtc = $updatedAtUtc,
                    FinishedAtUtc = CASE WHEN $isTerminal = 1 THEN $updatedAtUtc ELSE NULL END,
                    SuspensionKind = CASE WHEN $isTerminal = 1 THEN NULL ELSE SuspensionKind END,
                    ContinuationToken = CASE WHEN $isTerminal = 1 THEN NULL ELSE ContinuationToken END,
                    SuspensionDataJson = CASE WHEN $isTerminal = 1 THEN NULL ELSE SuspensionDataJson END
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Epoch = $expectedEpoch
                  AND FinishedAtUtc IS NULL
                  AND (
                      (Status IN ('Preparing', 'Idle') AND $status IN ('Running', 'Interrupted', 'Stopped', 'Failed'))
                      OR (Status = 'Running' AND $status IN ('Running', 'Interrupted', 'Stopped', 'Completed', 'Failed'))
                      OR (Status = 'WaitingForApproval' AND $status IN ('Interrupted', 'Stopped', 'Failed'))
                  )
                  AND (
                      $isTerminal = 1
                      OR NOT EXISTS (
                          SELECT 1 FROM AgentRuns newer
                          WHERE newer.SessionId = $sessionId
                            AND newer.RunRevision > $runRevision)
                  );
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$isTerminal", isTerminal ? 1 : 0);
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            key.SessionId,
            key.RunRevision,
            status,
            summary,
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        var run = GetRun(connection, transaction, key.RunId)
            ?? throw new InvalidOperationException("The transitioned durable run could not be reloaded.");
        transaction.Commit();
        return new AgentRunTransitionResult(run, checkpoint);
    }

    internal AgentRunSuspensionResult? SuspendRun(
        AgentDurableRunKey key,
        long expectedEpoch,
        AgentRunSuspension suspension,
        string? summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var result = SuspendRun(
            connection,
            transaction,
            key,
            expectedEpoch,
            suspension,
            summary,
            Guid.NewGuid().ToString("N"));
        if (result is null)
        {
            transaction.Rollback();
            return null;
        }

        transaction.Commit();
        return result;
    }

    internal AgentRunSuspensionResult? SuspendRun(
        AgentDurableRunKey key,
        AgentRunSuspension suspension,
        string? summary)
    {
        var run = GetRun(key.RunId);
        return run?.Key == key
            ? SuspendRun(key, run.Epoch, suspension, summary)
            : null;
    }

    internal AgentRunCheckpointRecord? ConsumeRunContinuation(
        AgentDurableRunKey key,
        long expectedEpoch,
        string continuationToken,
        AgentRunSuspensionKind suspensionKind,
        string? summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = 'Running',
                    UpdatedAtUtc = $updatedAtUtc,
                    SuspensionKind = NULL,
                    ContinuationToken = NULL,
                    SuspensionDataJson = NULL
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Epoch = $expectedEpoch
                  AND Status = 'WaitingForApproval'
                  AND FinishedAtUtc IS NULL
                  AND SuspensionKind = $suspensionKind
                  AND ContinuationToken = $continuationToken
                  AND NOT EXISTS (
                      SELECT 1 FROM AgentRuns newer
                      WHERE newer.SessionId = $sessionId
                        AND newer.RunRevision > $runRevision);
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
            command.Parameters.AddWithValue("$suspensionKind", suspensionKind.ToString());
            command.Parameters.AddWithValue("$continuationToken", continuationToken);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            key.SessionId,
            key.RunRevision,
            AgentRunStatus.Running,
            summary,
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        transaction.Commit();
        return checkpoint;
    }

    internal AgentRunCheckpointRecord? ConsumeRunContinuation(
        AgentDurableRunKey key,
        string continuationToken,
        AgentRunSuspensionKind suspensionKind,
        string? summary)
    {
        var run = GetRun(key.RunId);
        return run?.Key == key
            ? ConsumeRunContinuation(
                key,
                run.Epoch,
                continuationToken,
                suspensionKind,
                summary)
            : null;
    }

    internal AgentChildJoinTransitionResult CompleteChildJoinTask(
        AgentDurableRunKey key,
        long expectedEpoch,
        string continuationToken,
        AgentChildJoinTaskResult completedTask)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var run = GetRun(connection, transaction, key.RunId);
        if (run is null
            || run.Key != key
            || run.Epoch != expectedEpoch
            || run.Status != AgentDurableRunStatus.WaitingForApproval
            || run.FinishedAtUtc is not null
            || !string.Equals(run.ContinuationToken, continuationToken, StringComparison.Ordinal)
            || run.Suspension is not AgentChildJoinRunSuspension childJoin
            || HasNewerRun(connection, transaction, key.SessionId, key.RunRevision))
        {
            transaction.Rollback();
            return new AgentChildJoinTransitionResult(AgentChildJoinTransitionOutcome.Rejected);
        }

        var outstandingTask = childJoin.OutstandingTasks.FirstOrDefault(task =>
            task.ChildSessionId == completedTask.ChildSessionId
            && string.Equals(task.ToolCallId, completedTask.ToolCallId, StringComparison.Ordinal));
        if (outstandingTask is null)
        {
            transaction.Rollback();
            return new AgentChildJoinTransitionResult(AgentChildJoinTransitionOutcome.Rejected);
        }

        var updatedSuspension = childJoin with
        {
            OutstandingTasks = childJoin.OutstandingTasks
                .Where(task => task != outstandingTask)
                .ToArray(),
            CompletedTasks = childJoin.CompletedTasks.Append(completedTask).ToArray(),
        };
        var now = DateTimeOffset.UtcNow;
        var isReady = updatedSuspension.OutstandingTasks.Count == 0;
        var work = InsertParentContinuationWork(
            connection,
            transaction,
            key,
            completedTask,
            updatedSuspension,
            now);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    UpdatedAtUtc = $updatedAtUtc,
                    SuspensionDataJson = $suspensionDataJson
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Epoch = $expectedEpoch
                  AND Status = 'WaitingForApproval'
                  AND SuspensionKind = 'ChildJoin'
                  AND ContinuationToken = $continuationToken
                  AND FinishedAtUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
            command.Parameters.AddWithValue("$continuationToken", continuationToken);
            command.Parameters.AddWithValue(
                "$suspensionDataJson",
                AgentRunSuspensionSerializer.Serialize(updatedSuspension));

            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return new AgentChildJoinTransitionResult(AgentChildJoinTransitionOutcome.Rejected);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentParentContinuationWork
                SET Status = $status,
                    UpdatedAtUtc = $updatedAtUtc
                WHERE WorkId = $workId
                  AND Status = 'Pending';
                """;
            command.Parameters.AddWithValue(
                "$status",
                isReady
                    ? AgentParentContinuationWorkStatus.Ready.ToString()
                    : AgentParentContinuationWorkStatus.Completed.ToString());
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$workId", work.WorkId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new AgentChildJoinTransitionResult(
            isReady ? AgentChildJoinTransitionOutcome.Ready : AgentChildJoinTransitionOutcome.Waiting,
            updatedSuspension,
            Work: work with
            {
                Status = isReady
                    ? AgentParentContinuationWorkStatus.Ready
                    : AgentParentContinuationWorkStatus.Completed,
                UpdatedAtUtc = now,
            },
            Epoch: expectedEpoch + 1);
    }

    internal AgentChildJoinTransitionResult CompleteChildJoinTask(
        AgentDurableRunKey key,
        string continuationToken,
        AgentChildJoinTaskResult completedTask)
    {
        var run = GetRun(key.RunId);
        return run?.Key == key
            ? CompleteChildJoinTask(key, run.Epoch, continuationToken, completedTask)
            : new AgentChildJoinTransitionResult(AgentChildJoinTransitionOutcome.Rejected);
    }

    private static long GetNextRunRevision(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(RunRevision), 0)
            FROM (
                SELECT RunRevision FROM AgentRuns WHERE SessionId = $sessionId
                UNION ALL
                SELECT RunRevision FROM AgentRunCheckpoints WHERE SessionId = $sessionId
            );
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return Convert.ToInt64(command.ExecuteScalar()) + 1;
    }

    private static AgentRunSuspensionResult? SuspendRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        long expectedEpoch,
        AgentRunSuspension suspension,
        string? summary,
        string continuationToken)
    {
        var now = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = 'WaitingForApproval',
                    UpdatedAtUtc = $updatedAtUtc,
                    SuspensionKind = $suspensionKind,
                    ContinuationToken = $continuationToken,
                    SuspensionDataJson = $suspensionDataJson
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Epoch = $expectedEpoch
                  AND Status IN ('Preparing', 'Idle', 'Running')
                  AND FinishedAtUtc IS NULL
                  AND SuspensionKind IS NULL
                  AND ContinuationToken IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM AgentRuns newer
                      WHERE newer.SessionId = $sessionId
                        AND newer.RunRevision > $runRevision);
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$suspensionKind", suspension.Kind.ToString());
            command.Parameters.AddWithValue("$continuationToken", continuationToken);
            command.Parameters.AddWithValue("$suspensionDataJson", AgentRunSuspensionSerializer.Serialize(suspension));
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
            if (command.ExecuteNonQuery() != 1)
            {
                return null;
            }
        }

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            key.SessionId,
            key.RunRevision,
            AgentRunStatus.WaitingForApproval,
            summary,
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        return new AgentRunSuspensionResult(continuationToken, checkpoint);
    }

    private static AgentDurableRunRecord? GetRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT RunId, SessionId, RunRevision, Epoch, Status, ProfileId, UserMessage,
                   StartedAtUtc, UpdatedAtUtc, FinishedAtUtc, SuspensionKind,
                   ContinuationToken, SuspensionDataJson
            FROM AgentRuns
            WHERE RunId = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private static bool HasNewerRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        long runRevision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM AgentRuns
            WHERE SessionId = $sessionId
              AND RunRevision > $runRevision
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", runRevision);
        return command.ExecuteScalar() is not null;
    }

    private static void EnsureSessionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM AgentSessions WHERE SessionId = $sessionId LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        if (command.ExecuteScalar() is null)
        {
            throw new InvalidOperationException($"Session '{sessionId}' was not found.");
        }
    }

    private static AgentDurableRunRecord ReadRun(SqliteDataReader reader)
    {
        AgentRunSuspension? suspension = null;
        if (!reader.IsDBNull(10) && !reader.IsDBNull(12))
        {
            var kind = Enum.Parse<AgentRunSuspensionKind>(reader.GetString(10), ignoreCase: true);
            suspension = AgentRunSuspensionSerializer.Deserialize(kind, reader.GetString(12));
        }

        return new AgentDurableRunRecord(
            new AgentDurableRunKey(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2)),
            reader.GetInt64(3),
            Enum.Parse<AgentDurableRunStatus>(reader.GetString(4), ignoreCase: true),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            suspension,
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }
}

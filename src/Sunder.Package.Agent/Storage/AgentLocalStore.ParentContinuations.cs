using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const string ParentContinuationWorkColumns =
        "WorkId, ParentRunId, ParentSessionId, ParentRunRevision, ChildSessionId, ToolCallId, ChildStatus, Summary, Content, Title, SuspensionDataJson, Status, CreatedAtUtc, UpdatedAtUtc, ExecutionStartedAtUtc, LastError";

    internal IReadOnlyList<AgentParentContinuationWorkRecord> ListDispatchableParentContinuationWork()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ParentContinuationWorkColumns} FROM AgentParentContinuationWork WHERE Status = 'Ready' OR (Status = 'Dispatching' AND ExecutionStartedAtUtc IS NULL) ORDER BY CreatedAtUtc;";
        using var reader = command.ExecuteReader();
        var work = new List<AgentParentContinuationWorkRecord>();
        while (reader.Read())
        {
            work.Add(ReadParentContinuationWork(reader));
        }

        return work;
    }

    internal AgentParentContinuationDispatchResult? TryClaimParentContinuationWork(
        string workId,
        AgentDurableRunKey key,
        long expectedEpoch,
        string continuationToken)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var work = GetParentContinuationWork(connection, transaction, workId);
        var run = GetRun(connection, transaction, key.RunId);
        if (work?.Join is not { OutstandingTasks.Count: 0 } join
            || work.ParentRunKey != key
            || run?.Key != key
            || HasNewerRun(connection, transaction, key.SessionId, key.RunRevision))
        {
            transaction.Rollback();
            return null;
        }

        AgentRunCheckpointRecord runningCheckpoint;
        if (work.Status == AgentParentContinuationWorkStatus.Ready)
        {
            if (run.Epoch != expectedEpoch
                || run.Status != AgentDurableRunStatus.WaitingForApproval
                || run.Suspension is not AgentChildJoinRunSuspension
                || !string.Equals(run.ContinuationToken, continuationToken, StringComparison.Ordinal))
            {
                transaction.Rollback();
                return null;
            }

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
                       AND SuspensionKind = 'ChildJoin'
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
                command.Parameters.AddWithValue("$continuationToken", continuationToken);
                if (command.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return null;
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE AgentParentContinuationWork SET Status = 'Dispatching', UpdatedAtUtc = $updatedAtUtc WHERE WorkId = $workId AND Status = 'Ready';";
                command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
                command.Parameters.AddWithValue("$workId", workId);
                if (command.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return null;
                }
            }

            runningCheckpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                key.SessionId,
                key.RunRevision,
                AgentRunStatus.Running,
                "All subagent tasks reached a terminal state. Continuing provider execution.",
                now);
            InsertCheckpoint(connection, transaction, runningCheckpoint);
            TouchSessionForCheckpoint(connection, transaction, runningCheckpoint);
            work = work with
            {
                Status = AgentParentContinuationWorkStatus.Dispatching,
                UpdatedAtUtc = now,
            };
            run = GetRun(connection, transaction, key.RunId)!;
        }
        else if (work.Status == AgentParentContinuationWorkStatus.Dispatching
                  && work.ExecutionStartedAtUtc is null
                  && run.Status == AgentDurableRunStatus.Running
                  && run.FinishedAtUtc is null
                  && !HasNewerRun(connection, transaction, key.SessionId, key.RunRevision))
        {
            runningCheckpoint = GetLatestCheckpoint(connection, key.SessionId, transaction)
                ?? throw new InvalidOperationException("Dispatching parent continuation has no running checkpoint.");
        }
        else
        {
            transaction.Rollback();
            return null;
        }

        transaction.Commit();
        return new AgentParentContinuationDispatchResult(work, run, join, runningCheckpoint);
    }

    internal bool MarkParentContinuationExecutionStarted(string workId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentParentContinuationWork
            SET ExecutionStartedAtUtc = $startedAtUtc,
                UpdatedAtUtc = $startedAtUtc
            WHERE WorkId = $workId
              AND Status = 'Dispatching'
              AND ExecutionStartedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$startedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$workId", workId);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    internal bool CompleteParentContinuationWork(string workId, bool failed, string? error)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentParentContinuationWork
            SET Status = $status,
                UpdatedAtUtc = $updatedAtUtc,
                LastError = $lastError
            WHERE WorkId = $workId
              AND Status IN ('Ready', 'Dispatching');
            """;
        command.Parameters.AddWithValue(
            "$status",
            failed
                ? AgentParentContinuationWorkStatus.Failed.ToString()
                : AgentParentContinuationWorkStatus.Completed.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$workId", workId);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    internal bool RecordParentContinuationRetryPending(string workId, string error)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentParentContinuationWork
            SET UpdatedAtUtc = $updatedAtUtc,
                LastError = $lastError
            WHERE WorkId = $workId
              AND Status IN ('Ready', 'Dispatching')
              AND ExecutionStartedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$lastError", error);
        command.Parameters.AddWithValue("$workId", workId);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    internal void RecoverAmbiguousParentContinuationWork()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT {ParentContinuationWorkColumns} FROM AgentParentContinuationWork WHERE Status = 'Dispatching' ORDER BY CreatedAtUtc;";
        var workItems = new List<AgentParentContinuationWorkRecord>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                workItems.Add(ReadParentContinuationWork(reader));
            }
        }

        foreach (var work in workItems)
        {
            if (work.ExecutionStartedAtUtc is null)
            {
                // The durable claim is idempotently dispatchable until provider execution crosses this marker.
                continue;
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureRuntimeGenerationCurrent(connection, transaction);
            var now = DateTimeOffset.UtcNow;
            const string summary =
                "The prior process ended after parent continuation execution started; the provider outcome is ambiguous and the continuation will not be retried.";
            const AgentRunStatus runStatus = AgentRunStatus.Failed;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE AgentParentContinuationWork SET Status = 'Failed', UpdatedAtUtc = $updatedAtUtc, LastError = $summary WHERE WorkId = $workId AND Status = 'Dispatching';";
                command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
                command.Parameters.AddWithValue("$summary", summary);
                command.Parameters.AddWithValue("$workId", work.WorkId);
                if (command.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    continue;
                }
            }

            using var runCommand = connection.CreateCommand();
            runCommand.Transaction = transaction;
            runCommand.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = $status,
                    UpdatedAtUtc = $updatedAtUtc,
                    FinishedAtUtc = $updatedAtUtc,
                    SuspensionKind = NULL,
                    ContinuationToken = NULL,
                    SuspensionDataJson = NULL
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Status = 'Running'
                  AND FinishedAtUtc IS NULL;
                """;
            runCommand.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            runCommand.Parameters.AddWithValue("$status", runStatus.ToString());
            runCommand.Parameters.AddWithValue("$runId", work.ParentRunKey.RunId.ToString());
            runCommand.Parameters.AddWithValue("$sessionId", work.ParentRunKey.SessionId.ToString());
            runCommand.Parameters.AddWithValue("$runRevision", work.ParentRunKey.RunRevision);
            if (runCommand.ExecuteNonQuery() == 1)
            {
                CompleteStreamingTextTurns(
                    connection,
                    transaction,
                    work.ParentRunKey,
                    now);
                var checkpoint = new AgentRunCheckpointRecord(
                    Guid.NewGuid(),
                    work.ParentRunKey.SessionId,
                    work.ParentRunKey.RunRevision,
                    runStatus,
                    summary,
                    now);
                InsertCheckpoint(connection, transaction, checkpoint);
                TouchSessionForCheckpoint(connection, transaction, checkpoint);
                EnqueueRunLifecycleEvent(
                    connection,
                    transaction,
                    AgentLifecycleEventKind.RunFailed,
                    $"run:{work.ParentRunKey.RunId:N}:{work.ParentRunKey.RunRevision}:terminal:{runStatus}",
                    work.ParentRunKey,
                    checkpoint: checkpoint);
            }

            transaction.Commit();
        }
    }

    private static AgentParentContinuationWorkRecord InsertParentContinuationWork(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        AgentChildJoinTaskResult task,
        AgentChildJoinRunSuspension join,
        DateTimeOffset now)
    {
        var workId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.RunId:D}\n{task.ChildSessionId:D}\n{task.ToolCallId}"))).ToLowerInvariant();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO AgentParentContinuationWork (
                    WorkId, ParentRunId, ParentSessionId, ParentRunRevision, ChildSessionId,
                    ToolCallId, ChildStatus, Summary, Content, Title, SuspensionDataJson,
                    Status, CreatedAtUtc, UpdatedAtUtc)
                VALUES (
                    $workId, $parentRunId, $parentSessionId, $parentRunRevision,
                    $childSessionId, $toolCallId, $childStatus, $summary, $content, $title,
                    $suspensionDataJson, 'Pending', $createdAtUtc, $updatedAtUtc);
                """;
            command.Parameters.AddWithValue("$workId", workId);
            command.Parameters.AddWithValue("$parentRunId", key.RunId.ToString());
            command.Parameters.AddWithValue("$parentSessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$parentRunRevision", key.RunRevision);
            command.Parameters.AddWithValue("$childSessionId", task.ChildSessionId.ToString());
            command.Parameters.AddWithValue("$toolCallId", task.ToolCallId);
            command.Parameters.AddWithValue("$childStatus", task.Status.ToString());
            command.Parameters.AddWithValue("$summary", task.Summary);
            command.Parameters.AddWithValue("$content", (object?)task.Content ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", (object?)task.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$suspensionDataJson", AgentRunSuspensionSerializer.Serialize(join));
            command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.ExecuteNonQuery();
        }

        return GetParentContinuationWork(connection, transaction, workId)
            ?? throw new InvalidOperationException("Parent continuation work could not be persisted.");
    }

    private static AgentParentContinuationWorkRecord? GetParentContinuationWork(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ParentContinuationWorkColumns} FROM AgentParentContinuationWork WHERE WorkId = $workId;";
        command.Parameters.AddWithValue("$workId", workId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadParentContinuationWork(reader) : null;
    }

    private static AgentParentContinuationWorkRecord ReadParentContinuationWork(SqliteDataReader reader)
    {
        AgentChildJoinRunSuspension? join = null;
        if (!reader.IsDBNull(10))
        {
            join = (AgentChildJoinRunSuspension)AgentRunSuspensionSerializer.Deserialize(
                AgentRunSuspensionKind.ChildJoin,
                reader.GetString(10));
        }

        return new AgentParentContinuationWorkRecord(
            reader.GetString(0),
            new AgentDurableRunKey(
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetInt64(3)),
            Guid.Parse(reader.GetString(4)),
            reader.GetString(5),
            Enum.Parse<AgentRunStatus>(reader.GetString(6), ignoreCase: true),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            join,
            Enum.Parse<AgentParentContinuationWorkStatus>(reader.GetString(11), ignoreCase: true),
            DateTimeOffset.Parse(reader.GetString(12)),
            DateTimeOffset.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15));
    }
}

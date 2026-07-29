using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentUserTurnAdmissionResult AdmitUserTurn(AgentUserTurnAdmissionRequest request)
        => AdmitUserTurn(request, []);

    internal AgentUserTurnAdmissionResult AdmitUserTurn(
        AgentUserTurnAdmissionRequest request,
        IReadOnlyList<AgentSessionDataCleanerIdentity> activeCleaners)
    {
        ValidateAdmission(request);
        using var connection = CreateConnection();
        connection.Open();
        if (request.AdmissionKind == AgentRunAdmissionKind.Rollback)
        {
            EnableSecureDelete(connection);
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);

        if (GetRunByUserTurnId(connection, transaction, request.UserTurnId) is { } existing)
        {
            EnsureMatchingAdmission(existing, request);
            var existingTurn = GetTurn(connection, request.UserTurnId, transaction);
            var existingCheckpoint = GetRunCheckpoint(
                connection,
                transaction,
                existing.Key.SessionId,
                existing.Key.RunRevision)
                ?? CreateProjectedCheckpoint(existing);
            transaction.Commit();
            return new AgentUserTurnAdmissionResult(
                existing,
                existingTurn,
                existingCheckpoint,
                Rollback: null,
                IsExisting: true);
        }

        EnsureSessionMatchesWorkspace(connection, transaction, request.SessionId, request.WorkspaceId);
        AgentTranscriptRollbackResult? rollback = null;
        if (request.AdmissionKind == AgentRunAdmissionKind.Rollback)
        {
            rollback = RollbackTranscript(
                connection,
                transaction,
                request.SessionId,
                request.RollbackAnchorTurnId!.Value,
                activeCleaners);
        }

        var now = DateTimeOffset.UtcNow;
        var superseded = InterruptUnfinishedRunsForAdmission(
            connection,
            transaction,
            request.SessionId,
            now);
        var runRevision = GetNextRunRevision(connection, transaction, request.SessionId);
        var run = new AgentDurableRunRecord(
            new AgentDurableRunKey(Guid.NewGuid(), request.SessionId, runRevision),
            InitialRunEpoch,
            AgentDurableRunStatus.Preparing,
            request.ProfileId,
            request.UserMessage,
            now,
            now,
            FinishedAtUtc: null,
            UserTurnId: request.UserTurnId,
            WorkspaceId: request.WorkspaceId,
            AdmissionKind: request.AdmissionKind,
            RollbackAnchorTurnId: request.RollbackAnchorTurnId,
            RequestFingerprint: request.RequestFingerprint);
        InsertAdmittedRun(connection, transaction, run);

        var userTurn = request.Attachments.Count == 0
            ? CreateTextTurn(
                request.UserTurnId,
                request.SessionId,
                AgentMessageRole.User,
                AgentTurnKind.Message,
                request.UserMessage,
                now,
                now)
            : CreateMessageTurn(
                request.UserTurnId,
                request.SessionId,
                AgentMessageRole.User,
                request.UserMessage,
                request.Attachments,
                now,
                now);
        InsertTurn(connection, transaction, userTurn, runKey: run.Key);

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            request.SessionId,
            runRevision,
            AgentRunStatus.Idle,
            "User message admitted and queued for Runtime dispatch.",
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);

        if (rollback?.MemoryConsistencyBarrier is { } barrier)
        {
            using var barrierCommand = connection.CreateCommand();
            barrierCommand.Transaction = transaction;
            barrierCommand.CommandText = """
                UPDATE AgentRuns
                SET MemoryConsistencyBarrierEventId = $eventId,
                    MemoryConsistencyBarrierPayloadHash = $payloadHash
                WHERE RunId = $runId;
                """;
            barrierCommand.Parameters.AddWithValue("$eventId", barrier.EventId);
            barrierCommand.Parameters.AddWithValue("$payloadHash", barrier.PayloadHash);
            barrierCommand.Parameters.AddWithValue("$runId", run.Key.RunId.ToString());
            barrierCommand.ExecuteNonQuery();
        }

        EnqueueRunLifecycleEvent(
            connection,
            transaction,
            AgentLifecycleEventKind.UserTurnAdded,
            $"user-turn:{request.UserTurnId:N}",
            run.Key,
            triggerTurn: userTurn,
            checkpoint: checkpoint);

        transaction.Commit();
        if (rollback?.DeletedTurnIds.Count > 0)
        {
            CheckpointWriteAheadLog(connection);
        }
        if (rollback?.DeletedSessionIds.Count > 0)
        {
            SignalSessionCleanupJobsChanged();
        }
        return new AgentUserTurnAdmissionResult(
            GetRun(run.Key.RunId) ?? run,
            userTurn,
            checkpoint,
            rollback,
            IsExisting: false)
        {
            CompletedStreamingTurns = superseded.CompletedStreamingTurns,
            ToolResultTurns = superseded.ToolResultTurns,
        };
    }

    internal AgentDurableRunRecord? GetRunByUserTurnId(Guid userTurnId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DurableRunColumns} FROM AgentRuns WHERE UserTurnId = $userTurnId;";
        command.Parameters.AddWithValue("$userTurnId", userTurnId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    internal IReadOnlyList<AgentDurableRunRecord> ListPreparingAdmissions(int limit)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {DurableRunColumns}
            FROM AgentRuns
            WHERE Status = 'Preparing'
              AND FinishedAtUtc IS NULL
              AND UserTurnId IS NOT NULL
            ORDER BY StartedAtUtc, SessionId, RunRevision
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 256));
        using var reader = command.ExecuteReader();
        var runs = new List<AgentDurableRunRecord>();
        while (reader.Read())
        {
            runs.Add(ReadRun(reader));
        }
        return runs;
    }

    internal AgentRunTransitionResult? TryBeginAdmittedRunExecution(
        AgentDurableRunKey key,
        long expectedEpoch,
        string summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var now = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = 'Running',
                    ExecutionStartedAtUtc = $startedAtUtc,
                    UpdatedAtUtc = $startedAtUtc
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Epoch = $expectedEpoch
                  AND Status = 'Preparing'
                  AND FinishedAtUtc IS NULL
                  AND UserTurnId IS NOT NULL
                  AND ExecutionStartedAtUtc IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM AgentRuns newer
                      WHERE newer.SessionId = $sessionId
                        AND newer.RunRevision > $runRevision);
                """;
            command.Parameters.AddWithValue("$startedAtUtc", now.ToString("O"));
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
            AgentRunStatus.Running,
            summary,
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        var run = GetRun(connection, transaction, key.RunId)
            ?? throw new InvalidOperationException("The executing admission could not be reloaded.");
        transaction.Commit();
        return new AgentRunTransitionResult(run, checkpoint);
    }

    private static void ValidateAdmission(AgentUserTurnAdmissionRequest request)
    {
        if (request.UserTurnId == Guid.Empty)
        {
            throw new ArgumentException("User turn id cannot be empty.", nameof(request));
        }
        if (request.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.ProfileId)
            || string.IsNullOrWhiteSpace(request.WorkspaceId)
            || request.RequestFingerprint.Length != 64
            || !request.RequestFingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The user-turn admission identity is invalid.");
        }
        if ((request.AdmissionKind == AgentRunAdmissionKind.Rollback)
            != request.RollbackAnchorTurnId.HasValue)
        {
            throw new InvalidOperationException("Rollback admission requires exactly one rollback anchor.");
        }
    }

    private static AgentDurableRunRecord? GetRunByUserTurnId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid userTurnId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {DurableRunColumns} FROM AgentRuns WHERE UserTurnId = $userTurnId;";
        command.Parameters.AddWithValue("$userTurnId", userTurnId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private static void EnsureMatchingAdmission(
        AgentDurableRunRecord existing,
        AgentUserTurnAdmissionRequest request)
    {
        if (existing.UserTurnId != request.UserTurnId
            || existing.Key.SessionId != request.SessionId
            || !string.Equals(existing.ProfileId, request.ProfileId, StringComparison.Ordinal)
            || !string.Equals(existing.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(existing.UserMessage, request.UserMessage, StringComparison.Ordinal)
            || existing.AdmissionKind != request.AdmissionKind
            || existing.RollbackAnchorTurnId != request.RollbackAnchorTurnId
            || !string.Equals(
                existing.RequestFingerprint,
                request.RequestFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentUserTurnConflictException(request.UserTurnId);
        }
    }

    private static void EnsureSessionMatchesWorkspace(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT WorkspaceId FROM AgentSessions WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        var value = command.ExecuteScalar();
        if (value is not string sessionWorkspaceId)
        {
            throw new InvalidOperationException($"Session '{sessionId}' was not found.");
        }
        if (!string.Equals(sessionWorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected session belongs to a different workspace.");
        }
    }

    private static void InsertAdmittedRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunRecord run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentRuns (
                RunId, SessionId, RunRevision, Epoch, Status, ProfileId, UserMessage,
                StartedAtUtc, UpdatedAtUtc, FinishedAtUtc, UserTurnId, WorkspaceId,
                AdmissionKind, RollbackAnchorTurnId, RequestFingerprint, ExecutionStartedAtUtc)
            VALUES (
                $runId, $sessionId, $runRevision, $epoch, 'Preparing', $profileId,
                $userMessage, $startedAtUtc, $updatedAtUtc, NULL, $userTurnId,
                $workspaceId, $admissionKind, $rollbackAnchorTurnId,
                $requestFingerprint, NULL);
            """;
        command.Parameters.AddWithValue("$runId", run.Key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", run.Key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", run.Key.RunRevision);
        command.Parameters.AddWithValue("$epoch", run.Epoch);
        command.Parameters.AddWithValue("$profileId", run.ProfileId);
        command.Parameters.AddWithValue("$userMessage", run.UserMessage);
        command.Parameters.AddWithValue("$startedAtUtc", run.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", run.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$userTurnId", run.UserTurnId!.Value.ToString());
        command.Parameters.AddWithValue("$workspaceId", run.WorkspaceId!);
        command.Parameters.AddWithValue("$admissionKind", run.AdmissionKind!.Value.ToString());
        command.Parameters.AddWithValue(
            "$rollbackAnchorTurnId",
            run.RollbackAnchorTurnId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$requestFingerprint", run.RequestFingerprint!);
        command.ExecuteNonQuery();
    }

    private AgentRunSupersessionResult InterruptUnfinishedRunsForAdmission(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        DateTimeOffset now)
    {
        var runs = new List<AgentDurableRunRecord>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {DurableRunColumns} FROM AgentRuns WHERE SessionId = $sessionId AND FinishedAtUtc IS NULL ORDER BY RunRevision;";
            select.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(ReadRun(reader));
            }
        }

        var completedTurns = new List<AgentCompletedStreamingTurn>();
        var toolResultTurns = new List<AgentTurnRecord>();
        foreach (var run in runs)
        {
            var summary = "Superseded by a newer admitted user turn.";
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE AgentRuns
                    SET Epoch = Epoch + 1,
                        Status = 'Interrupted',
                        UpdatedAtUtc = $now,
                        FinishedAtUtc = $now,
                        SuspensionKind = NULL,
                        ContinuationToken = NULL,
                        SuspensionDataJson = NULL
                    WHERE RunId = $runId AND FinishedAtUtc IS NULL;
                    """;
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$runId", run.Key.RunId.ToString());
                if (update.ExecuteNonQuery() != 1)
                {
                    continue;
                }
            }

            completedTurns.AddRange(CompleteStreamingTextTurns(
                connection,
                transaction,
                run.Key,
                now));
            toolResultTurns.AddRange(TerminalizeOpenToolExecutions(
                connection,
                transaction,
                run.Key,
                AgentRunStatus.Interrupted,
                now));
            TerminalizeAdmissionPermissions(connection, transaction, run.Key, summary, now);
            TerminalizeParentContinuationWork(connection, transaction, run.Key, summary, now);

            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                sessionId,
                run.Key.RunRevision,
                AgentRunStatus.Interrupted,
                summary,
                now);
            InsertCheckpoint(connection, transaction, checkpoint);
            TouchSessionForCheckpoint(connection, transaction, checkpoint);
            EnqueueRunLifecycleEvent(
                connection,
                transaction,
                AgentLifecycleEventKind.RunInterrupted,
                $"run:{run.Key.RunId:N}:{run.Key.RunRevision}:terminal:Interrupted",
                run.Key,
                checkpoint: checkpoint);
        }

        return new AgentRunSupersessionResult(completedTurns, toolResultTurns);
    }

    private static void TerminalizeAdmissionPermissions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        string summary,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentPendingPermissionRequests
            SET Status = CASE
                    WHEN ToolExecutionId IS NOT NULL AND EXISTS (
                        SELECT 1 FROM AgentToolExecutions execution
                        WHERE execution.ExecutionId = AgentPendingPermissionRequests.ToolExecutionId
                          AND execution.Status = 'Completed') THEN 'Executed'
                    WHEN ToolExecutionId IS NOT NULL AND EXISTS (
                        SELECT 1 FROM AgentToolExecutions execution
                        WHERE execution.ExecutionId = AgentPendingPermissionRequests.ToolExecutionId
                          AND execution.Status IN ('Started', 'Failed', 'Ambiguous')) THEN 'Failed'
                    WHEN ToolExecutionId IS NULL AND ExecutionStartedAtUtc IS NOT NULL THEN 'Failed'
                    ELSE 'Expired'
                END,
                DecidedAtUtc = $now,
                DecisionSummary = $summary,
                ClaimLeaseExpiresAtUtc = NULL
            WHERE RunId = $runId
              AND SessionId = $sessionId
              AND RunRevision = $runRevision
              AND Status IN ('Pending', 'Claimed');
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
        command.ExecuteNonQuery();
    }

    private static void TerminalizeParentContinuationWork(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        string summary,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentParentContinuationWork
            SET Status = 'Failed', UpdatedAtUtc = $now, LastError = $summary
            WHERE ParentRunId = $runId
              AND ParentSessionId = $sessionId
              AND ParentRunRevision = $runRevision
              AND Status IN ('Pending', 'Ready', 'Dispatching');
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
        command.ExecuteNonQuery();
    }

    private static AgentRunCheckpointRecord? GetRunCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        long runRevision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CheckpointId, SessionId, RunRevision, Status, Summary, CreatedAtUtc
            FROM AgentRunCheckpoints
            WHERE SessionId = $sessionId AND RunRevision = $runRevision
            ORDER BY CreatedAtUtc DESC, CheckpointId DESC
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

    private static AgentRunCheckpointRecord CreateProjectedCheckpoint(AgentDurableRunRecord run)
    {
        var status = run.Status == AgentDurableRunStatus.Preparing
            ? AgentRunStatus.Idle
            : Enum.Parse<AgentRunStatus>(run.Status.ToString(), ignoreCase: false);
        return new AgentRunCheckpointRecord(
            Guid.Empty,
            run.Key.SessionId,
            run.Key.RunRevision,
            status,
            status == AgentRunStatus.Idle
                ? "User message was admitted and is queued for Runtime dispatch."
                : "Run admission remains durably correlated.",
            run.UpdatedAtUtc);
    }

    private sealed record AgentRunSupersessionResult(
        IReadOnlyList<AgentCompletedStreamingTurn> CompletedStreamingTurns,
        IReadOnlyList<AgentTurnRecord> ToolResultTurns);
}

using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal bool CompleteClaimedPermissionRequest(
        Guid sessionId,
        string requestId,
        string claimToken,
        AgentPendingPermissionStatus status,
        string summary)
    {
        if (status is AgentPendingPermissionStatus.Pending or AgentPendingPermissionStatus.Claimed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "A claimed request must complete with a terminal status.");
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentPendingPermissionRequests
            SET Status = $status,
                DecidedAtUtc = $decidedAtUtc,
                DecisionSummary = $summary,
                ClaimLeaseExpiresAtUtc = NULL
            WHERE SessionId = $sessionId
              AND RequestId = $requestId
              AND Status = 'Claimed'
              AND ClaimToken = $claimToken;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$decidedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$claimToken", claimToken);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    internal AgentPermissionExpirationResult ExpireActivePermissionRequest(
        Guid sessionId,
        string requestId,
        string summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var request = GetPermissionRequest(connection, sessionId, requestId, transaction);
        if (request?.Status is not (AgentPendingPermissionStatus.Pending
                or AgentPendingPermissionStatus.Claimed))
        {
            transaction.Rollback();
            return new AgentPermissionExpirationResult(false);
        }

        var now = DateTimeOffset.UtcNow;
        var ledgerExecution = request.ToolExecutionId is { } toolExecutionId
            ? GetToolExecution(connection, transaction, toolExecutionId)
            : null;
        var ledgerStatus = ledgerExecution?.Status;
        var ambiguous = request.ToolExecutionId is not null
            ? ledgerStatus is AgentToolExecutionStatus.Started or AgentToolExecutionStatus.Ambiguous
            : request.ExecutionStartedAtUtc is not null;
        var terminalStatus = request.ToolExecutionId is not null
            ? ledgerStatus switch
            {
                AgentToolExecutionStatus.Completed => AgentPendingPermissionStatus.Executed,
                AgentToolExecutionStatus.Prepared => AgentPendingPermissionStatus.Expired,
                _ => AgentPendingPermissionStatus.Failed,
            }
            : ambiguous
                ? AgentPendingPermissionStatus.Failed
                : AgentPendingPermissionStatus.Expired;
        var terminalSummary = ambiguous
            ? "Run stopped after approved tool execution started; the external mutation outcome is ambiguous and will not be retried."
            : ledgerExecution?.OutcomeSummary ?? summary;
        AgentRunCheckpointRecord? checkpoint = null;
        IReadOnlyList<AgentCompletedStreamingTurn> completedStreamingTurns = [];
        if (!string.IsNullOrWhiteSpace(request.ContinuationToken)
            && TryFinalizePermissionRun(
                connection,
                transaction,
                request,
                AgentRunStatus.Interrupted,
                terminalSummary,
                now) is { } completed)
        {
            completedStreamingTurns = completed;
            checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                sessionId,
                request.RunRevision,
                AgentRunStatus.Interrupted,
                terminalSummary,
                now);
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentPendingPermissionRequests
                SET Status = $status,
                    DecidedAtUtc = $decidedAtUtc,
                    DecisionSummary = $summary,
                    ClaimLeaseExpiresAtUtc = NULL
                WHERE SessionId = $sessionId
                  AND RequestId = $requestId
                  AND Status IN ('Pending', 'Claimed');
                """;
            command.Parameters.AddWithValue("$status", terminalStatus.ToString());
            command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$summary", terminalSummary);
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$requestId", requestId);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return new AgentPermissionExpirationResult(false);
            }
        }

        IReadOnlyList<AgentTurnRecord> toolResultTurns = [];
        if (checkpoint is not null)
        {
            toolResultTurns = TerminalizeOpenToolExecutions(
                connection,
                transaction,
                new AgentDurableRunKey(request.RunId, request.SessionId, request.RunRevision),
                AgentRunStatus.Interrupted,
                now);
            InsertCheckpoint(connection, transaction, checkpoint);
            TouchSessionForCheckpoint(connection, transaction, checkpoint);
            EnqueueRunLifecycleEvent(
                connection,
                transaction,
                AgentLifecycleEventKind.RunInterrupted,
                $"run:{request.RunId:N}:{request.RunRevision}:terminal:Interrupted",
                new AgentDurableRunKey(request.RunId, request.SessionId, request.RunRevision),
                checkpoint: checkpoint);
        }

        transaction.Commit();
        return new AgentPermissionExpirationResult(
            true,
            checkpoint is null
                ? null
                : new AgentCheckpointPersistenceResult(
                    checkpoint,
                    completedStreamingTurns)
                {
                    ToolResultTurns = toolResultTurns,
                });
    }

    internal AgentRunStopPersistenceResult? TryStopRunAndActivePermissions(
        AgentDurableRunKey key,
        long expectedEpoch,
        string summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var now = DateTimeOffset.UtcNow;
        var completedStreamingTurns = CompleteStreamingTextTurns(
            connection,
            transaction,
            key,
            now);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = 'Stopped',
                    UpdatedAtUtc = $updatedAtUtc,
                    FinishedAtUtc = $updatedAtUtc,
                    SuspensionKind = NULL,
                    ContinuationToken = NULL,
                    SuspensionDataJson = NULL
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Epoch = $expectedEpoch
                  AND Status IN ('Preparing', 'Idle', 'Running', 'WaitingForApproval')
                  AND FinishedAtUtc IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentRuns newer
                      WHERE newer.SessionId = AgentRuns.SessionId
                        AND newer.RunRevision > AgentRuns.RunRevision);
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
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

        using (var command = connection.CreateCommand())
        {
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
                        WHEN ToolExecutionId IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM AgentToolExecutions execution
                            WHERE execution.ExecutionId = AgentPendingPermissionRequests.ToolExecutionId) THEN 'Failed'
                        WHEN ToolExecutionId IS NULL AND ExecutionStartedAtUtc IS NOT NULL THEN 'Failed'
                        ELSE 'Expired'
                    END,
                    DecidedAtUtc = $decidedAtUtc,
                    DecisionSummary = CASE
                        WHEN ToolExecutionId IS NOT NULL AND EXISTS (
                            SELECT 1 FROM AgentToolExecutions execution
                            WHERE execution.ExecutionId = AgentPendingPermissionRequests.ToolExecutionId
                              AND execution.Status IN ('Started', 'Ambiguous'))
                            THEN 'Run stopped after approved tool execution started; effects may have occurred and no retry happened.'
                        WHEN ToolExecutionId IS NULL AND ExecutionStartedAtUtc IS NOT NULL
                            THEN 'Run stopped after approved tool execution started; effects may have occurred and no retry happened.'
                        ELSE $summary
                    END,
                    ClaimLeaseExpiresAtUtc = NULL
                WHERE RunId = $runId
                  AND SessionId = $sessionId
                  AND RunRevision = $runRevision
                  AND Status IN ('Pending', 'Claimed');
                """;
            command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            command.ExecuteNonQuery();
        }

        var toolResultTurns = TerminalizeOpenToolExecutions(
            connection,
            transaction,
            key,
            AgentRunStatus.Stopped,
            now);

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            key.SessionId,
            key.RunRevision,
            AgentRunStatus.Stopped,
            summary,
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        var run = GetRun(connection, transaction, key.RunId)!;
        EnqueueRunLifecycleEvent(
            connection,
            transaction,
            AgentLifecycleEventKind.RunStopped,
            $"run:{key.RunId:N}:{key.RunRevision}:terminal:Stopped",
            key,
            checkpoint: checkpoint);
        transaction.Commit();
        return new AgentRunStopPersistenceResult(
            new AgentRunTransitionResult(run, checkpoint)
            {
                ToolResultTurns = toolResultTurns,
            },
            completedStreamingTurns);
    }

    internal void RecoverInterruptedPermissionClaims()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE Status IN ('Pending', 'Claimed') ORDER BY CreatedAtUtc;";
        var requests = new List<AgentPendingPermissionRequestRecord>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                requests.Add(ReadPendingPermissionRequest(reader));
            }
        }

        foreach (var request in requests)
        {
            var ledgerExecution = request.ToolExecutionId is { } toolExecutionId
                ? GetToolExecution(toolExecutionId)
                : null;
            var lacksDurableIdentity = request.RunId == Guid.Empty
                || request.RunRevision <= 0
                || string.IsNullOrWhiteSpace(request.ExecutionFingerprint)
                || string.IsNullOrWhiteSpace(request.ExecutionSnapshotJson)
                || string.IsNullOrWhiteSpace(request.ContinuationToken)
                || request.ToolExecutionId is not null && ledgerExecution is null
                || request.ResourceClaimSetVersion < 0
                || string.Equals(
                       request.BoundaryId,
                       AgentPermissionBoundaryIds.OutsideConfiguredScope,
                       StringComparison.OrdinalIgnoreCase)
                   && (request.ResourceClaimSetVersion != 1 || request.ResourceClaims.Count == 0);
            var hasRecoverablePreparedExecution = request.ToolExecutionId is null
                || ledgerExecution?.Status == AgentToolExecutionStatus.Prepared;
            if (request.Status == AgentPendingPermissionStatus.Pending
                && !lacksDurableIdentity
                && hasRecoverablePreparedExecution)
            {
                continue;
            }

            var ambiguous = request.ToolExecutionId is not null
                ? ledgerExecution?.Status is AgentToolExecutionStatus.Started
                    or AgentToolExecutionStatus.Ambiguous
                : request.Status == AgentPendingPermissionStatus.Claimed
                  && (request.ContinuationConsumedAtUtc is not null
                      || request.ExecutionStartedAtUtc is not null);
            if (request.Status == AgentPendingPermissionStatus.Claimed
                && !ambiguous
                && (request.ToolExecutionId is null
                    || !lacksDurableIdentity
                       && hasRecoverablePreparedExecution
                       && request.ContinuationConsumedAtUtc is null))
            {
                // An unconsumed claim remains recoverable after its persisted lease expires.
                continue;
            }
            var permissionStatus = request.ToolExecutionId is not null
                ? ledgerExecution?.Status == AgentToolExecutionStatus.Completed
                    ? AgentPendingPermissionStatus.Executed
                    : AgentPendingPermissionStatus.Failed
                : ambiguous
                    ? AgentPendingPermissionStatus.Failed
                    : AgentPendingPermissionStatus.Expired;
            var runStatus = ambiguous && request.ToolExecutionId is null
                ? AgentRunStatus.Failed
                : AgentRunStatus.Interrupted;
            var recoverySummary = ambiguous
                ? request.ToolExecutionId is null
                    ? "The prior process ended after consuming an approved permission continuation; the external mutation outcome is ambiguous and will not be retried."
                    : "The prior process ended after approved tool dispatch; the outcome is ambiguous, effects may have occurred, and no retry happened."
                : ledgerExecution?.Status switch
                {
                    AgentToolExecutionStatus.Completed => ledgerExecution.OutcomeSummary
                        ?? "The approved tool completed before startup recovery interrupted provider continuation.",
                    AgentToolExecutionStatus.Failed => ledgerExecution.OutcomeSummary
                        ?? "The approved tool failed before startup recovery interrupted provider continuation.",
                    _ when request.ResourceClaimSetVersion < 0 =>
                        "Legacy v3 transient resource authority cannot be restored; explicit reapproval is required.",
                    _ when lacksDurableIdentity =>
                        "Legacy permission request expired because it lacks durable run identity, fingerprint, or continuation state.",
                    _ when request.ToolExecutionId is not null =>
                        "The approved tool call did not dispatch before the prior process ended and was not retried.",
                    _ => "Permission claim expired because its continuation was never consumed before the prior process ended.",
                };
            RecoverPermissionRequest(
                connection,
                request,
                permissionStatus,
                runStatus,
                recoverySummary);
        }
    }

    private void RecoverPermissionRequest(
        SqliteConnection connection,
        AgentPendingPermissionRequestRecord request,
        AgentPendingPermissionStatus permissionStatus,
        AgentRunStatus runStatus,
        string summary)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;
        var runChanged = false;
        if (request.RunId != Guid.Empty && request.RunRevision > 0)
        {
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
                  AND Status IN ('Preparing', 'Idle', 'Running', 'WaitingForApproval')
                  AND FinishedAtUtc IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM AgentRuns newer
                      WHERE newer.SessionId = $sessionId
                        AND newer.RunRevision > $runRevision);
                """;
            runCommand.Parameters.AddWithValue("$status", runStatus.ToString());
            runCommand.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            runCommand.Parameters.AddWithValue("$runId", request.RunId.ToString());
            runCommand.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            runCommand.Parameters.AddWithValue("$runRevision", request.RunRevision);
            runChanged = runCommand.ExecuteNonQuery() == 1;
            if (runChanged)
            {
                var key = new AgentDurableRunKey(
                    request.RunId,
                    request.SessionId,
                    request.RunRevision);
                CompleteStreamingTextTurns(
                    connection,
                    transaction,
                    key,
                    now);
                TerminalizeOpenToolExecutions(
                    connection,
                    transaction,
                    key,
                    runStatus,
                    now);
            }
        }

        using (var requestCommand = connection.CreateCommand())
        {
            requestCommand.Transaction = transaction;
            requestCommand.CommandText = """
                UPDATE AgentPendingPermissionRequests
                SET Status = $status,
                    DecidedAtUtc = $decidedAtUtc,
                    DecisionSummary = $summary,
                    ClaimLeaseExpiresAtUtc = NULL
                WHERE SessionId = $sessionId
                  AND RequestId = $requestId
                  AND Status IN ('Pending', 'Claimed');
                """;
            requestCommand.Parameters.AddWithValue("$status", permissionStatus.ToString());
            requestCommand.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            requestCommand.Parameters.AddWithValue("$summary", summary);
            requestCommand.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            requestCommand.Parameters.AddWithValue("$requestId", request.RequestId);
            if (requestCommand.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return;
            }
        }

        var latest = GetLatestCheckpoint(connection, request.SessionId, transaction);
        if (runChanged
            || latest is { Status: AgentRunStatus.WaitingForApproval }
               && (request.RunRevision <= 0 || latest.RunRevision == request.RunRevision))
        {
            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                request.SessionId,
                request.RunRevision > 0 ? request.RunRevision : latest!.RunRevision,
                runStatus,
                summary,
                now);
            InsertCheckpoint(connection, transaction, checkpoint);
            TouchSessionForCheckpoint(connection, transaction, checkpoint);
            if (request.RunId != Guid.Empty && request.RunRevision > 0)
            {
                var key = new AgentDurableRunKey(request.RunId, request.SessionId, request.RunRevision);
                if (GetRun(connection, transaction, request.RunId) is not null
                    && TryMapTerminalLifecycleKind(runStatus, out var lifecycleKind))
                {
                    EnqueueRunLifecycleEvent(
                        connection,
                        transaction,
                        lifecycleKind,
                        $"run:{request.RunId:N}:{request.RunRevision}:terminal:{runStatus}",
                        key,
                        checkpoint: checkpoint);
                }
            }
        }

        transaction.Commit();
    }
}

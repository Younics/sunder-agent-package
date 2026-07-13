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
        using var command = connection.CreateCommand();
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
        return command.ExecuteNonQuery() == 1;
    }

    internal bool ExpireActivePermissionRequest(Guid sessionId, string requestId, string summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var request = GetPermissionRequest(connection, sessionId, requestId, transaction);
        if (request?.Status is not (AgentPendingPermissionStatus.Pending
                or AgentPendingPermissionStatus.Claimed))
        {
            transaction.Rollback();
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var ambiguous = request.ExecutionStartedAtUtc is not null;
        var terminalStatus = ambiguous
            ? AgentPendingPermissionStatus.Failed
            : AgentPendingPermissionStatus.Expired;
        var terminalSummary = ambiguous
            ? "Run stopped after approved tool execution started; the external mutation outcome is ambiguous and will not be retried."
            : summary;
        AgentRunCheckpointRecord? checkpoint = null;
        if (!string.IsNullOrWhiteSpace(request.ContinuationToken)
            && TryFinalizePermissionRun(
                connection,
                transaction,
                request,
                AgentRunStatus.Interrupted,
                terminalSummary,
                now))
        {
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
                return false;
            }
        }

        if (checkpoint is not null)
        {
            InsertCheckpoint(connection, transaction, checkpoint);
            TouchSessionForCheckpoint(connection, transaction, checkpoint);
        }

        transaction.Commit();
        return true;
    }

    internal AgentRunTransitionResult? TryStopRunAndActivePermissions(
        AgentDurableRunKey key,
        long expectedEpoch,
        string summary)
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
                  AND FinishedAtUtc IS NULL;
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
                SET Status = CASE WHEN ExecutionStartedAtUtc IS NULL THEN 'Expired' ELSE 'Failed' END,
                    DecidedAtUtc = $decidedAtUtc,
                    DecisionSummary = CASE
                        WHEN ExecutionStartedAtUtc IS NULL THEN $summary
                        ELSE 'Run stopped after approved tool execution started; the external mutation outcome is ambiguous and will not be retried.'
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
        transaction.Commit();
        return new AgentRunTransitionResult(run, checkpoint);
    }

    private void RecoverInterruptedPermissionClaims()
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
            var lacksDurableIdentity = request.RunId == Guid.Empty
                || request.RunRevision <= 0
                || string.IsNullOrWhiteSpace(request.ExecutionFingerprint)
                || string.IsNullOrWhiteSpace(request.ExecutionSnapshotJson)
                || string.IsNullOrWhiteSpace(request.ContinuationToken);
            if (request.Status == AgentPendingPermissionStatus.Pending && !lacksDurableIdentity)
            {
                continue;
            }

            var ambiguous = request.Status == AgentPendingPermissionStatus.Claimed
                && (request.ContinuationConsumedAtUtc is not null
                    || request.ExecutionStartedAtUtc is not null);
            if (request.Status == AgentPendingPermissionStatus.Claimed && !ambiguous)
            {
                // An unconsumed claim remains recoverable after its persisted lease expires.
                continue;
            }
            var permissionStatus = ambiguous
                ? AgentPendingPermissionStatus.Failed
                : AgentPendingPermissionStatus.Expired;
            var runStatus = ambiguous ? AgentRunStatus.Failed : AgentRunStatus.Interrupted;
            var recoverySummary = ambiguous
                ? "The prior process ended after consuming an approved permission continuation; the external mutation outcome is ambiguous and will not be retried."
                : lacksDurableIdentity
                    ? "Legacy permission request expired because it lacks durable run identity, fingerprint, or continuation state."
                    : "Permission claim expired because its continuation was never consumed before the prior process ended.";
            RecoverPermissionRequest(
                connection,
                request,
                permissionStatus,
                runStatus,
                recoverySummary);
        }
    }

    private static void RecoverPermissionRequest(
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
                  AND FinishedAtUtc IS NULL;
                """;
            runCommand.Parameters.AddWithValue("$status", runStatus.ToString());
            runCommand.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            runCommand.Parameters.AddWithValue("$runId", request.RunId.ToString());
            runCommand.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            runCommand.Parameters.AddWithValue("$runRevision", request.RunRevision);
            runChanged = runCommand.ExecuteNonQuery() == 1;
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
        }

        transaction.Commit();
    }
}

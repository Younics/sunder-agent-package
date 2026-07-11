using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const string PendingPermissionColumns = "RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage, CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path, WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation, CreatedAtUtc, ParentSessionId, RootSessionId, Status, ClaimToken, ClaimedAtUtc, DecidedAtUtc, DecisionSummary, ExecutionFingerprint, ContinuationToken, ClaimLeaseExpiresAtUtc, ContinuationConsumedAtUtc, ExecutionStartedAtUtc";

    private static readonly TimeSpan PermissionClaimLeaseDuration = TimeSpan.FromMinutes(5);

    public IReadOnlyList<AgentPermissionOverride> ListPermissionOverrides()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ActionId, BoundaryId, Decision, UpdatedAtUtc FROM AgentPermissionOverrides ORDER BY ActionId, BoundaryId;";

        using var reader = command.ExecuteReader();
        var overrides = new List<AgentPermissionOverride>();
        while (reader.Read())
        {
            overrides.Add(new AgentPermissionOverride(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<AgentPermissionDecision>(reader.GetString(2), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return overrides;
    }

    public void SavePermissionOverride(AgentPermissionOverride permissionOverride)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentPermissionOverrides (ActionId, BoundaryId, Decision, UpdatedAtUtc)
            VALUES ($actionId, $boundaryId, $decision, $updatedAtUtc)
            ON CONFLICT(ActionId, BoundaryId) DO UPDATE SET
                Decision = excluded.Decision,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$actionId", permissionOverride.ActionId);
        command.Parameters.AddWithValue("$boundaryId", permissionOverride.BoundaryId);
        command.Parameters.AddWithValue("$decision", permissionOverride.Decision.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", permissionOverride.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeletePermissionOverride(string actionId, string boundaryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AgentPermissionOverrides WHERE ActionId = $actionId AND BoundaryId = $boundaryId;";
        command.Parameters.AddWithValue("$actionId", actionId);
        command.Parameters.AddWithValue("$boundaryId", boundaryId);
        command.ExecuteNonQuery();
    }

    public AgentSessionPermissionState GetSessionPermissionState(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsUnrestrictedModeEnabled FROM AgentSessionPermissionStates WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        var value = command.ExecuteScalar();
        return new AgentSessionPermissionState(sessionId, value is not null && Convert.ToInt64(value) != 0);
    }

    public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentSessionPermissionStates (SessionId, IsUnrestrictedModeEnabled)
            VALUES ($sessionId, $isEnabled)
            ON CONFLICT(SessionId) DO UPDATE SET IsUnrestrictedModeEnabled = excluded.IsUnrestrictedModeEnabled;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AgentSessionPermissionApproval> ListSessionPermissionApprovals(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ApprovalId, SessionId, ActionId, MatcherKind, Pattern, CreatedAtUtc FROM AgentSessionPermissionApprovals WHERE SessionId = $sessionId ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        var approvals = new List<AgentSessionPermissionApproval>();
        while (reader.Read())
        {
            approvals.Add(new AgentSessionPermissionApproval(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<AgentPermissionMatcherKind>(reader.GetString(3), ignoreCase: true),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return approvals;
    }

    public void SaveSessionPermissionApproval(AgentSessionPermissionApproval approval)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO AgentSessionPermissionApprovals (ApprovalId, SessionId, ActionId, MatcherKind, Pattern, CreatedAtUtc) VALUES ($approvalId, $sessionId, $actionId, $matcherKind, $pattern, $createdAtUtc);";
        command.Parameters.AddWithValue("$approvalId", approval.ApprovalId);
        command.Parameters.AddWithValue("$sessionId", approval.SessionId.ToString());
        command.Parameters.AddWithValue("$actionId", approval.ActionId);
        command.Parameters.AddWithValue("$matcherKind", approval.MatcherKind.ToString());
        command.Parameters.AddWithValue("$pattern", approval.Pattern);
        command.Parameters.AddWithValue("$createdAtUtc", approval.CreatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public AgentPendingPermissionRequestRecord SavePendingPermissionRequest(AgentPendingPermissionRequestRecord record)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO AgentPendingPermissionRequests (RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage, CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path, WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation, CreatedAtUtc, ParentSessionId, RootSessionId, Status, ClaimToken, ClaimedAtUtc, DecidedAtUtc, DecisionSummary, ExecutionFingerprint, ContinuationToken, ClaimLeaseExpiresAtUtc, ContinuationConsumedAtUtc, ExecutionStartedAtUtc)
            VALUES ($requestId, $sessionId, $runId, $runRevision, $profileId, $userTurnId, $userMessage, $callId, $actionId, $boundaryId, $summary, $toolId, $argumentsJson, $command, $path, $workspaceId, $bindingId, $resourceDisplayName, $resourceReference, $isMutation, $createdAtUtc, $parentSessionId, $rootSessionId, $status, $claimToken, $claimedAtUtc, $decidedAtUtc, $decisionSummary, $executionFingerprint, $continuationToken, $claimLeaseExpiresAtUtc, $continuationConsumedAtUtc, $executionStartedAtUtc);
            """;
        command.Parameters.AddWithValue("$requestId", record.RequestId);
        command.Parameters.AddWithValue("$sessionId", record.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", record.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", record.RunRevision);
        command.Parameters.AddWithValue("$profileId", (object?)record.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$userTurnId", record.UserTurnId.ToString());
        command.Parameters.AddWithValue("$userMessage", record.UserMessage);
        command.Parameters.AddWithValue("$callId", record.CallId);
        command.Parameters.AddWithValue("$actionId", record.ActionId);
        command.Parameters.AddWithValue("$boundaryId", record.BoundaryId);
        command.Parameters.AddWithValue("$summary", record.Summary);
        command.Parameters.AddWithValue("$toolId", (object?)record.ToolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", record.ArgumentsJson);
        command.Parameters.AddWithValue("$command", (object?)record.Command ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)record.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bindingId", (object?)record.BindingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$resourceDisplayName", (object?)record.ResourceDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$resourceReference", (object?)record.ResourceReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$isMutation", record.IsMutation ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$parentSessionId", record.ParentSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rootSessionId", record.RootSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$claimToken", (object?)record.ClaimToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$claimedAtUtc", record.ClaimedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$decidedAtUtc", record.DecidedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$decisionSummary", (object?)record.DecisionSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionFingerprint", record.ExecutionFingerprint);
        command.Parameters.AddWithValue("$continuationToken", (object?)record.ContinuationToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$claimLeaseExpiresAtUtc", record.ClaimLeaseExpiresAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$continuationConsumedAtUtc", record.ContinuationConsumedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$executionStartedAtUtc", record.ExecutionStartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        if (command.ExecuteNonQuery() != 0)
        {
            return record;
        }

        using var existingCommand = connection.CreateCommand();
        existingCommand.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RunId = $runId AND RunRevision = $runRevision AND CallId = $callId AND Status IN ('Pending', 'Claimed') ORDER BY CreatedAtUtc DESC LIMIT 1;";
        existingCommand.Parameters.AddWithValue("$sessionId", record.SessionId.ToString());
        existingCommand.Parameters.AddWithValue("$runId", record.RunId.ToString());
        existingCommand.Parameters.AddWithValue("$runRevision", record.RunRevision);
        existingCommand.Parameters.AddWithValue("$callId", record.CallId);
        using var reader = existingCommand.ExecuteReader();
        return reader.Read()
            ? ReadPendingPermissionRequest(reader)
            : throw new InvalidOperationException("The pending permission request could not be persisted.");
    }

    internal AgentPendingPermissionRequestRecord? SavePendingPermissionRequestAndSuspendRun(
        AgentPendingPermissionRequestRecord record,
        long expectedEpoch)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var continuationToken = Guid.NewGuid().ToString("N");
        var suspension = new AgentPermissionRunSuspension(
            record.RequestId,
            record.CallId,
            record.UserTurnId);
        var suspended = SuspendRun(
            connection,
            transaction,
            new AgentDurableRunKey(record.RunId, record.SessionId, record.RunRevision),
            expectedEpoch,
            suspension,
            record.Summary,
            continuationToken);
        if (suspended is null)
        {
            transaction.Rollback();
            return null;
        }

        var persisted = record with { ContinuationToken = continuationToken };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentPendingPermissionRequests (
                RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage,
                CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path,
                WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation,
                CreatedAtUtc, ParentSessionId, RootSessionId, Status, ClaimToken, ClaimedAtUtc,
                DecidedAtUtc, DecisionSummary, ExecutionFingerprint, ContinuationToken,
                ClaimLeaseExpiresAtUtc, ContinuationConsumedAtUtc, ExecutionStartedAtUtc)
            VALUES (
                $requestId, $sessionId, $runId, $runRevision, $profileId, $userTurnId,
                $userMessage, $callId, $actionId, $boundaryId, $summary, $toolId,
                $argumentsJson, $command, $path, $workspaceId, $bindingId,
                $resourceDisplayName, $resourceReference, $isMutation, $createdAtUtc,
                $parentSessionId, $rootSessionId, $status, NULL, NULL, NULL, NULL,
                $executionFingerprint, $continuationToken, NULL, NULL, NULL);
            """;
        AddPendingPermissionParameters(command, persisted);
        if (command.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return null;
        }

        transaction.Commit();
        return persisted;
    }

    internal AgentPendingPermissionRequestRecord? SavePendingPermissionRequestAndSuspendRun(
        AgentPendingPermissionRequestRecord record)
    {
        var run = GetRun(record.RunId);
        return run is null
            ? null
            : SavePendingPermissionRequestAndSuspendRun(record, run.Epoch);
    }

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingPermissionRequests(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND Status IN ('Pending', 'Claimed') ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        var requests = new List<AgentPendingPermissionRequestRecord>();
        while (reader.Read())
        {
            requests.Add(ReadPendingPermissionRequest(reader));
        }

        return requests;
    }

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingPermissionRequestsForSessionTree(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        var session = GetSession(sessionId);
        var rootSessionId = session?.RootSessionId ?? sessionId;

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE Status IN ('Pending', 'Claimed') AND (SessionId = $sessionId OR RootSessionId = $rootSessionId OR ParentSessionId = $sessionId) ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$rootSessionId", rootSessionId.ToString());

        using var reader = command.ExecuteReader();
        var requests = new List<AgentPendingPermissionRequestRecord>();
        while (reader.Read())
        {
            requests.Add(ReadPendingPermissionRequest(reader));
        }

        return requests;
    }

    public AgentPendingPermissionRequestRecord? GetPendingPermissionRequest(Guid sessionId, string requestId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RequestId = $requestId AND Status IN ('Pending', 'Claimed');";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPendingPermissionRequest(reader) : null;
    }

    internal AgentPendingPermissionRequestRecord? GetPermissionRequest(Guid sessionId, string requestId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetPermissionRequest(connection, sessionId, requestId);
    }

    internal AgentPendingPermissionClaimResult TryClaimPendingPermissionRequest(Guid sessionId, string requestId)
    {
        using var connection = CreateConnection();
        connection.Open();

        var claimToken = Guid.NewGuid().ToString("N");
        var claimedAtUtc = DateTimeOffset.UtcNow;
        var leaseExpiresAtUtc = claimedAtUtc.Add(PermissionClaimLeaseDuration);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE AgentPendingPermissionRequests
            SET Status = 'Claimed',
                ClaimToken = $claimToken,
                ClaimedAtUtc = $claimedAtUtc,
                ClaimLeaseExpiresAtUtc = $claimLeaseExpiresAtUtc
            WHERE SessionId = $sessionId
              AND RequestId = $requestId
              AND (
                  Status = 'Pending'
                  OR (
                      Status = 'Claimed'
                      AND ClaimLeaseExpiresAtUtc IS NOT NULL
                      AND ClaimLeaseExpiresAtUtc <= $claimedAtUtc
                      AND ContinuationConsumedAtUtc IS NULL
                      AND ExecutionStartedAtUtc IS NULL))
              AND ContinuationToken IS NOT NULL
              AND EXISTS (
                  SELECT 1
                  FROM AgentRuns
                  WHERE AgentRuns.RunId = AgentPendingPermissionRequests.RunId
                    AND AgentRuns.SessionId = AgentPendingPermissionRequests.SessionId
                    AND AgentRuns.RunRevision = AgentPendingPermissionRequests.RunRevision
                    AND AgentRuns.Status = 'WaitingForApproval'
                    AND AgentRuns.FinishedAtUtc IS NULL
                    AND AgentRuns.SuspensionKind = 'Permission'
                    AND AgentRuns.ContinuationToken = AgentPendingPermissionRequests.ContinuationToken
                    AND NOT EXISTS (
                        SELECT 1 FROM AgentRuns newer
                        WHERE newer.SessionId = AgentPendingPermissionRequests.SessionId
                          AND newer.RunRevision > AgentPendingPermissionRequests.RunRevision))
            RETURNING {PendingPermissionColumns};
            """;
        command.Parameters.AddWithValue("$claimToken", claimToken);
        command.Parameters.AddWithValue("$claimedAtUtc", claimedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$claimLeaseExpiresAtUtc", leaseExpiresAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                return new AgentPendingPermissionClaimResult(
                    AgentPendingPermissionClaimOutcome.Claimed,
                    ReadPendingPermissionRequest(reader));
            }
        }

        var existing = GetPermissionRequest(connection, sessionId, requestId);
        return existing?.Status switch
        {
            AgentPendingPermissionStatus.Claimed => new AgentPendingPermissionClaimResult(
                AgentPendingPermissionClaimOutcome.AlreadyClaimed,
                existing),
            AgentPendingPermissionStatus.Pending => new AgentPendingPermissionClaimResult(
                AgentPendingPermissionClaimOutcome.InvalidSuspension,
                existing),
            null => new AgentPendingPermissionClaimResult(AgentPendingPermissionClaimOutcome.NotFound),
            _ => new AgentPendingPermissionClaimResult(
                AgentPendingPermissionClaimOutcome.AlreadyDecided,
                existing),
        };
    }

    internal AgentRunCheckpointRecord? ResumeClaimedPermissionRequest(
        AgentPendingPermissionRequestRecord request,
        long expectedEpoch)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken)
            || string.IsNullOrWhiteSpace(request.ContinuationToken))
        {
            return null;
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var persisted = GetPermissionRequest(connection, request.SessionId, request.RequestId, transaction);
        if (persisted?.Status != AgentPendingPermissionStatus.Claimed
            || !string.Equals(persisted.ClaimToken, request.ClaimToken, StringComparison.Ordinal)
            || !string.Equals(persisted.ContinuationToken, request.ContinuationToken, StringComparison.Ordinal))
        {
            transaction.Rollback();
            return null;
        }

        var run = GetRun(connection, transaction, request.RunId);
        if (run?.Suspension is not AgentPermissionRunSuspension suspension
            || !string.Equals(suspension.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(suspension.ToolCallId, request.CallId, StringComparison.Ordinal)
            || suspension.UserTurnId != request.UserTurnId)
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
                  AND SuspensionKind = 'Permission'
                  AND ContinuationToken = $continuationToken
                  AND NOT EXISTS (
                      SELECT 1 FROM AgentRuns newer
                      WHERE newer.SessionId = $sessionId
                        AND newer.RunRevision > $runRevision);
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$runId", request.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", request.RunRevision);
            command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
            command.Parameters.AddWithValue("$continuationToken", request.ContinuationToken);
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
                SET ContinuationConsumedAtUtc = $consumedAtUtc,
                    ClaimLeaseExpiresAtUtc = $claimLeaseExpiresAtUtc
                WHERE SessionId = $sessionId
                  AND RequestId = $requestId
                  AND Status = 'Claimed'
                  AND ClaimToken = $claimToken
                  AND ContinuationToken = $continuationToken
                  AND ContinuationConsumedAtUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$consumedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$claimLeaseExpiresAtUtc", now.Add(PermissionClaimLeaseDuration).ToString("O"));
            command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            command.Parameters.AddWithValue("$requestId", request.RequestId);
            command.Parameters.AddWithValue("$claimToken", request.ClaimToken);
            command.Parameters.AddWithValue("$continuationToken", request.ContinuationToken);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            request.SessionId,
            request.RunRevision,
            AgentRunStatus.Running,
            "Permission approved. Resuming the suspended run.",
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        transaction.Commit();
        return checkpoint;
    }

    internal bool MarkClaimedPermissionExecutionStarted(
        Guid sessionId,
        string requestId,
        string claimToken)
    {
        using var connection = CreateConnection();
        connection.Open();
        var now = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentPendingPermissionRequests
            SET ExecutionStartedAtUtc = $executionStartedAtUtc,
                ClaimLeaseExpiresAtUtc = $claimLeaseExpiresAtUtc
            WHERE SessionId = $sessionId
              AND RequestId = $requestId
              AND Status = 'Claimed'
              AND ClaimToken = $claimToken
              AND ContinuationConsumedAtUtc IS NOT NULL
              AND ExecutionStartedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$executionStartedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$claimLeaseExpiresAtUtc", now.Add(PermissionClaimLeaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$claimToken", claimToken);
        return command.ExecuteNonQuery() == 1;
    }

    internal AgentRunCheckpointRecord? FinalizeClaimedPermissionRequest(
        AgentPendingPermissionRequestRecord request,
        AgentPendingPermissionStatus status,
        AgentRunStatus runStatus,
        string summary)
    {
        if (status is AgentPendingPermissionStatus.Pending or AgentPendingPermissionStatus.Claimed
            || runStatus is not (AgentRunStatus.Failed or AgentRunStatus.Stopped or AgentRunStatus.Interrupted)
            || string.IsNullOrWhiteSpace(request.ClaimToken)
            || string.IsNullOrWhiteSpace(request.ContinuationToken))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A claimed permission suspension must be finalized with terminal states.");
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;

        if (!TryFinalizePermissionRun(
                connection,
                transaction,
                request,
                runStatus,
                summary,
                now))
        {
            transaction.Rollback();
            return null;
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
                  AND Status = 'Claimed'
                  AND ClaimToken = $claimToken
                  AND ContinuationToken = $continuationToken;
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            command.Parameters.AddWithValue("$requestId", request.RequestId);
            command.Parameters.AddWithValue("$claimToken", request.ClaimToken);
            command.Parameters.AddWithValue("$continuationToken", request.ContinuationToken);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            request.SessionId,
            request.RunRevision,
            runStatus,
            summary,
            now);
        InsertCheckpoint(connection, transaction, checkpoint);
        TouchSessionForCheckpoint(connection, transaction, checkpoint);
        transaction.Commit();
        return checkpoint;
    }

    internal AgentPendingPermissionDecisionResult TryDenyPendingPermissionRequest(
        Guid sessionId,
        string requestId,
        string summary)
        => TryDecidePendingPermissionRequest(
            sessionId,
            requestId,
            AgentPendingPermissionStatus.Denied,
            summary);

    public void DeletePendingPermissionRequest(Guid sessionId, string requestId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RequestId = $requestId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        command.ExecuteNonQuery();
    }

    private AgentPendingPermissionDecisionResult TryDecidePendingPermissionRequest(
        Guid sessionId,
        string requestId,
        AgentPendingPermissionStatus status,
        string summary)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = GetPermissionRequest(connection, sessionId, requestId, transaction);
        if (existing?.Status == AgentPendingPermissionStatus.Pending
            && !string.IsNullOrWhiteSpace(existing.ContinuationToken))
        {
            var now = DateTimeOffset.UtcNow;
            var checkpoint = status == AgentPendingPermissionStatus.Denied
                && TryFinalizePermissionRun(
                    connection,
                    transaction,
                    existing,
                    AgentRunStatus.Stopped,
                    summary,
                    now)
                    ? new AgentRunCheckpointRecord(
                        Guid.NewGuid(),
                        sessionId,
                        existing.RunRevision,
                        AgentRunStatus.Stopped,
                        summary,
                        now)
                    : null;
            if (checkpoint is not null)
            {
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
                      AND Status = 'Pending'
                      AND ContinuationToken = $continuationToken;
                    """;
                command.Parameters.AddWithValue("$status", status.ToString());
                command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
                command.Parameters.AddWithValue("$summary", summary);
                command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
                command.Parameters.AddWithValue("$requestId", requestId);
                command.Parameters.AddWithValue("$continuationToken", existing.ContinuationToken);
                if (command.ExecuteNonQuery() == 1)
                {
                    InsertCheckpoint(connection, transaction, checkpoint);
                    TouchSessionForCheckpoint(connection, transaction, checkpoint);
                    transaction.Commit();
                    return new AgentPendingPermissionDecisionResult(
                        AgentPendingPermissionDecisionOutcome.Decided,
                        existing with
                        {
                            Status = status,
                            DecidedAtUtc = now,
                            DecisionSummary = summary,
                        },
                        checkpoint);
                }
            }
        }

        transaction.Rollback();
        return existing?.Status switch
        {
            AgentPendingPermissionStatus.Claimed => new AgentPendingPermissionDecisionResult(
                AgentPendingPermissionDecisionOutcome.AlreadyClaimed,
                existing),
            null => new AgentPendingPermissionDecisionResult(AgentPendingPermissionDecisionOutcome.NotFound),
            _ => new AgentPendingPermissionDecisionResult(
                AgentPendingPermissionDecisionOutcome.AlreadyDecided,
                existing),
        };
    }

    private static AgentPendingPermissionRequestRecord? GetPermissionRequest(
        SqliteConnection connection,
        Guid sessionId,
        string requestId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RequestId = $requestId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPendingPermissionRequest(reader) : null;
    }

    private static AgentPendingPermissionRequestRecord ReadPendingPermissionRequest(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Guid.Parse(reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.GetInt64(19) != 0,
            DateTimeOffset.Parse(reader.GetString(20)),
            reader.IsDBNull(21) ? null : Guid.Parse(reader.GetString(21)),
            reader.IsDBNull(22) ? null : Guid.Parse(reader.GetString(22)),
            Enum.Parse<AgentPendingPermissionStatus>(reader.GetString(23), ignoreCase: true),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : DateTimeOffset.Parse(reader.GetString(25)),
            reader.IsDBNull(26) ? null : DateTimeOffset.Parse(reader.GetString(26)),
            reader.IsDBNull(27) ? null : reader.GetString(27),
            reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetString(29),
            reader.IsDBNull(30) ? null : DateTimeOffset.Parse(reader.GetString(30)),
            reader.IsDBNull(31) ? null : DateTimeOffset.Parse(reader.GetString(31)),
            reader.IsDBNull(32) ? null : DateTimeOffset.Parse(reader.GetString(32)));

    private static void AddPendingPermissionParameters(
        SqliteCommand command,
        AgentPendingPermissionRequestRecord record)
    {
        command.Parameters.AddWithValue("$requestId", record.RequestId);
        command.Parameters.AddWithValue("$sessionId", record.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", record.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", record.RunRevision);
        command.Parameters.AddWithValue("$profileId", (object?)record.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$userTurnId", record.UserTurnId.ToString());
        command.Parameters.AddWithValue("$userMessage", record.UserMessage);
        command.Parameters.AddWithValue("$callId", record.CallId);
        command.Parameters.AddWithValue("$actionId", record.ActionId);
        command.Parameters.AddWithValue("$boundaryId", record.BoundaryId);
        command.Parameters.AddWithValue("$summary", record.Summary);
        command.Parameters.AddWithValue("$toolId", (object?)record.ToolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", record.ArgumentsJson);
        command.Parameters.AddWithValue("$command", (object?)record.Command ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)record.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bindingId", (object?)record.BindingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$resourceDisplayName", (object?)record.ResourceDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$resourceReference", (object?)record.ResourceReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$isMutation", record.IsMutation ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$parentSessionId", record.ParentSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rootSessionId", record.RootSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$executionFingerprint", record.ExecutionFingerprint);
        command.Parameters.AddWithValue("$continuationToken", (object?)record.ContinuationToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$claimLeaseExpiresAtUtc", record.ClaimLeaseExpiresAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$continuationConsumedAtUtc", record.ContinuationConsumedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$executionStartedAtUtc", record.ExecutionStartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
    }

    private static bool TryFinalizePermissionRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentPendingPermissionRequestRecord request,
        AgentRunStatus runStatus,
        string summary,
        DateTimeOffset now)
    {
        _ = summary;
        var run = GetRun(connection, transaction, request.RunId);
        if (run?.Key != new AgentDurableRunKey(
                request.RunId,
                request.SessionId,
                request.RunRevision))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentRuns
            SET Epoch = Epoch + 1,
                Status = $status,
                UpdatedAtUtc = $updatedAtUtc,
                FinishedAtUtc = $finishedAtUtc,
                SuspensionKind = NULL,
                ContinuationToken = NULL,
                SuspensionDataJson = NULL
            WHERE RunId = $runId
              AND SessionId = $sessionId
              AND RunRevision = $runRevision
              AND Epoch = $expectedEpoch
              AND Status = 'WaitingForApproval'
              AND FinishedAtUtc IS NULL
              AND SuspensionKind = 'Permission'
              AND ContinuationToken = $continuationToken;
            """;
        command.Parameters.AddWithValue("$status", runStatus.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$finishedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$runId", request.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", request.RunRevision);
        command.Parameters.AddWithValue("$expectedEpoch", run.Epoch);
        command.Parameters.AddWithValue("$continuationToken", request.ContinuationToken!);
        return command.ExecuteNonQuery() == 1;
    }

}

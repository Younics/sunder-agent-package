using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static void AddPendingPermissionParameters(SqliteCommand command, AgentPendingPermissionRequestRecord record)
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
        command.Parameters.AddWithValue("$executionSnapshotJson", record.ExecutionSnapshotJson);
        command.Parameters.AddWithValue("$toolExecutionId", record.ToolExecutionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$resourceClaimSetVersion", record.ResourceClaimSetVersion);
        command.Parameters.AddWithValue("$resourceClaimsJson", SerializeResourceClaims(record.ResourceClaims));
    }

    private static IReadOnlyList<AgentCompletedStreamingTurn>? TryFinalizePermissionRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentPendingPermissionRequestRecord request,
        AgentRunStatus runStatus,
        string summary,
        DateTimeOffset now)
    {
        _ = summary;
        var run = GetRun(connection, transaction, request.RunId);
        if (run?.Key != new AgentDurableRunKey(request.RunId, request.SessionId, request.RunRevision))
        {
            return null;
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
              AND ContinuationToken = $continuationToken
              AND NOT EXISTS (
                  SELECT 1 FROM AgentRuns newer
                  WHERE newer.SessionId = $sessionId
                    AND newer.RunRevision > $runRevision);
            """;
        command.Parameters.AddWithValue("$status", runStatus.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$finishedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$runId", request.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", request.RunRevision);
        command.Parameters.AddWithValue("$expectedEpoch", run.Epoch);
        command.Parameters.AddWithValue("$continuationToken", request.ContinuationToken!);
        if (command.ExecuteNonQuery() != 1)
        {
            return null;
        }

        return IsFinishedRunStatus(runStatus)
            ? CompleteStreamingTextTurns(
                connection,
                transaction,
                run.Key,
                now)
            : [];
    }
}

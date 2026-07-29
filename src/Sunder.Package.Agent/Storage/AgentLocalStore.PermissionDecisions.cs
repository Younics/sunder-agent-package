using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
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
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var existing = GetPermissionRequest(connection, sessionId, requestId, transaction);
        if (existing?.Status == AgentPendingPermissionStatus.Pending
            && !string.IsNullOrWhiteSpace(existing.ContinuationToken))
        {
            var now = DateTimeOffset.UtcNow;
            AgentToolExecutionCompletionResult? ledgerCompletion = null;
            if (status == AgentPendingPermissionStatus.Denied
                && existing.ToolExecutionId is { } toolExecutionId)
            {
                var deniedResult = new AgentToolResult(
                    existing.ToolId ?? string.Empty,
                    summary,
                    Content: $"Permission denied: tool '{existing.ToolId}' was not executed.",
                    IsError: true,
                    ErrorCode: "permission-denied");
                ledgerCompletion = TryCompleteToolExecution(
                    connection,
                    transaction,
                    new AgentDurableRunKey(
                        existing.RunId,
                        existing.SessionId,
                        existing.RunRevision),
                    toolExecutionId,
                    AgentToolExecutionStatus.Failed,
                    deniedResult,
                    "permission-denied",
                    allowPreparedReadOnlyCompletion: false,
                    now: now);
                if (ledgerCompletion is null)
                {
                    transaction.Rollback();
                    return new AgentPendingPermissionDecisionResult(
                        AgentPendingPermissionDecisionOutcome.AlreadyDecided,
                        existing);
                }
            }
            var completedStreamingTurns = status == AgentPendingPermissionStatus.Denied
                ? TryFinalizePermissionRun(
                    connection,
                    transaction,
                    existing,
                    AgentRunStatus.Stopped,
                    summary,
                    now)
                : null;
            var terminalizedToolResults = completedStreamingTurns is not null
                ? TerminalizeOpenToolExecutions(
                    connection,
                    transaction,
                    new AgentDurableRunKey(
                        existing.RunId,
                        existing.SessionId,
                        existing.RunRevision),
                    AgentRunStatus.Stopped,
                    now)
                : [];
            var checkpoint = completedStreamingTurns is not null
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
                    var toolResultTurn = ledgerCompletion?.ToolResultTurn
                        ?? CreateToolResultTurn(
                            Guid.NewGuid(),
                            sessionId,
                            existing.CallId,
                            existing.ToolId ?? string.Empty,
                            existing.ArgumentsJson,
                            $"Permission denied: tool '{existing.ToolId}' was not executed.",
                            summary,
                            structuredPayloadJson: null,
                            sourcesJson: null,
                            wasTruncated: false,
                            isError: true,
                            errorCode: "permission-denied",
                            backendId: null,
                            presentationPayloadJson: null,
                            now,
                            now) with
                        {
                            RunId = existing.RunId,
                            RunRevision = existing.RunRevision,
                        };
                    if (ledgerCompletion is null)
                    {
                        InsertTurn(
                            connection,
                            transaction,
                            toolResultTurn,
                            runKey: new AgentDurableRunKey(
                                existing.RunId,
                                existing.SessionId,
                                existing.RunRevision));
                    }
                    InsertCheckpoint(connection, transaction, checkpoint);
                    TouchSessionForCheckpoint(connection, transaction, checkpoint);
                    EnqueueRunLifecycleEvent(
                        connection,
                        transaction,
                        AgentLifecycleEventKind.RunStopped,
                        $"run:{existing.RunId:N}:{existing.RunRevision}:terminal:Stopped",
                        new AgentDurableRunKey(existing.RunId, existing.SessionId, existing.RunRevision),
                        triggerTurn: toolResultTurn,
                        checkpoint: checkpoint);
                    transaction.Commit();
                    return new AgentPendingPermissionDecisionResult(
                        AgentPendingPermissionDecisionOutcome.Decided,
                        existing with
                        {
                            Status = status,
                            DecidedAtUtc = now,
                            DecisionSummary = summary,
                        },
                        new AgentCheckpointPersistenceResult(
                            checkpoint,
                            completedStreamingTurns!)
                        {
                            ToolResultTurns = terminalizedToolResults,
                        },
                        toolResultTurn);
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
}

using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal void RecoverToolExecutions()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var open = ListAllOpenToolExecutions(connection, transaction);
        var dispatchedRunKeys = new HashSet<AgentDurableRunKey>();
        var now = DateTimeOffset.UtcNow;
        foreach (var execution in open)
        {
            if (execution.Status == AgentToolExecutionStatus.Prepared
                && IsValidPreparedPermissionSuspension(connection, transaction, execution))
            {
                continue;
            }

            var ambiguous = execution.Status == AgentToolExecutionStatus.Started;
            var status = ambiguous
                ? AgentToolExecutionStatus.Ambiguous
                : AgentToolExecutionStatus.Failed;
            var summary = ambiguous
                ? "The prior process ended after tool dispatch. Effects may have occurred; no retry happened."
                : "The prior process ended before this tool call was dispatched; it was not retried.";
            var result = new AgentToolResult(
                execution.ToolId,
                summary,
                Content: ambiguous
                    ? "### Tool execution outcome is ambiguous\n\nEffects may have occurred. Sunder did not retry this tool call."
                    : "### Tool call not executed\n\nThe prior process ended before dispatch. Sunder did not retry this tool call.",
                IsError: true,
                ErrorCode: ambiguous ? "tool-execution-ambiguous" : "tool-not-dispatched");
            var key = new AgentDurableRunKey(
                execution.RunId,
                execution.SessionId,
                execution.RunRevision);
            _ = TryCompleteToolExecution(
                    connection,
                    transaction,
                    key,
                    execution.ExecutionId,
                    status,
                    result,
                    result.ErrorCode!,
                    allowPreparedReadOnlyCompletion: false,
                    now: now)
                ?? throw new InvalidOperationException(
                    $"Startup recovery could not terminalize tool execution '{execution.ExecutionId}'.");
            if (ambiguous)
            {
                dispatchedRunKeys.Add(key);
            }
        }

        foreach (var key in dispatchedRunKeys)
        {
            _ = TerminalizeOpenToolExecutions(
                connection,
                transaction,
                key,
                AgentRunStatus.Interrupted,
                now);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentRuns
                SET Epoch = Epoch + 1,
                    Status = 'Interrupted',
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
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$runId", key.RunId.ToString());
            command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
            command.Parameters.AddWithValue("$runRevision", key.RunRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                continue;
            }

            CompleteStreamingTextTurns(connection, transaction, key, now);
            var summary = "The prior process ended after tool dispatch; effects may have occurred and no retry happened.";
            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                key.SessionId,
                key.RunRevision,
                AgentRunStatus.Interrupted,
                summary,
                now);
            InsertCheckpoint(connection, transaction, checkpoint);
            TouchSessionForCheckpoint(connection, transaction, checkpoint);
            EnqueueRunLifecycleEvent(
                connection,
                transaction,
                AgentLifecycleEventKind.RunInterrupted,
                $"run:{key.RunId:N}:{key.RunRevision}:terminal:Interrupted",
                key,
                checkpoint: checkpoint);
        }

        transaction.Commit();
    }

    private IReadOnlyList<AgentTurnRecord> TerminalizeOpenToolExecutions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        AgentRunStatus runStatus,
        DateTimeOffset now)
    {
        var executions = ListOpenToolExecutions(connection, transaction, key);
        var resultTurns = new List<AgentTurnRecord>(executions.Count);
        foreach (var execution in executions)
        {
            var ambiguous = execution.Status == AgentToolExecutionStatus.Started;
            var status = ambiguous
                ? AgentToolExecutionStatus.Ambiguous
                : AgentToolExecutionStatus.Failed;
            var summary = ambiguous
                ? "Tool execution was interrupted after dispatch. Effects may have occurred; no retry happened."
                : $"Tool call was not dispatched because the run ended as {runStatus}.";
            var result = new AgentToolResult(
                execution.ToolId,
                summary,
                Content: ambiguous
                    ? "### Tool execution outcome is ambiguous\n\nEffects may have occurred. Sunder did not retry this tool call."
                    : $"### Tool call not executed\n\n{summary}",
                IsError: true,
                ErrorCode: ambiguous ? "tool-execution-ambiguous" : "tool-not-dispatched");
            var completion = TryCompleteToolExecution(
                connection,
                transaction,
                key,
                execution.ExecutionId,
                status,
                result,
                result.ErrorCode!,
                allowPreparedReadOnlyCompletion: false,
                now: now)
                ?? throw new InvalidOperationException(
                    $"Open tool execution '{execution.ExecutionId}' could not be terminalized.");
            resultTurns.Add(completion.ToolResultTurn);
        }
        return resultTurns;
    }

    private static bool HasOpenToolExecutions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM AgentToolExecutions
            WHERE RunId = $runId
              AND SessionId = $sessionId
              AND RunRevision = $runRevision
              AND Status IN ('Prepared', 'Started')
            LIMIT 1;
            """;
        AddRunKeyParameters(command, key);
        return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<AgentToolExecutionRecord> ListOpenToolExecutions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ToolExecutionColumns} FROM AgentToolExecutions WHERE RunId = $runId AND SessionId = $sessionId AND RunRevision = $runRevision AND Status IN ('Prepared', 'Started') ORDER BY PreparedAtUtc, ExecutionId;";
        AddRunKeyParameters(command, key);
        using var reader = command.ExecuteReader();
        var executions = new List<AgentToolExecutionRecord>();
        while (reader.Read())
        {
            executions.Add(ReadToolExecution(reader));
        }
        return executions;
    }

    private static IReadOnlyList<AgentToolExecutionRecord> ListAllOpenToolExecutions(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ToolExecutionColumns} FROM AgentToolExecutions WHERE Status IN ('Prepared', 'Started') ORDER BY PreparedAtUtc, ExecutionId;";
        using var reader = command.ExecuteReader();
        var executions = new List<AgentToolExecutionRecord>();
        while (reader.Read())
        {
            executions.Add(ReadToolExecution(reader));
        }
        return executions;
    }

    private static bool IsValidPreparedPermissionSuspension(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentToolExecutionRecord execution)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM AgentPendingPermissionRequests permission
            INNER JOIN AgentRuns run
                ON run.RunId = permission.RunId
               AND run.SessionId = permission.SessionId
               AND run.RunRevision = permission.RunRevision
            WHERE permission.ToolExecutionId = $executionId
              AND permission.SessionId = $sessionId
              AND permission.RunId = $runId
              AND permission.RunRevision = $runRevision
              AND permission.CallId = $callId
              AND permission.ToolId = $toolId
              AND permission.Status IN ('Pending', 'Claimed')
              AND permission.ContinuationConsumedAtUtc IS NULL
              AND run.Status = 'WaitingForApproval'
              AND run.FinishedAtUtc IS NULL
              AND run.SuspensionKind = 'Permission'
              AND run.ContinuationToken = permission.ContinuationToken
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString());
        command.Parameters.AddWithValue("$sessionId", execution.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", execution.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", execution.RunRevision);
        command.Parameters.AddWithValue("$callId", execution.CallId);
        command.Parameters.AddWithValue("$toolId", execution.ToolId);
        return command.ExecuteScalar() is not null;
    }
}

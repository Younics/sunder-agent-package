using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentTurnRecord? TryReplaceChildSuspensionToolResult(
        AgentDurableRunKey key,
        long expectedEpoch,
        string callId,
        AgentToolResult result)
    {
        BeforeFencedTranscriptTransaction?.Invoke(AgentTranscriptMutationKind.ToolResult);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        if (!CanMutateTranscript(connection, transaction, key, expectedEpoch))
        {
            transaction.Rollback();
            return null;
        }

        var execution = GetToolExecutionByCallId(
            connection,
            transaction,
            key.RunId,
            key.RunRevision,
            callId);
        if (execution is null
            || execution.SessionId != key.SessionId
            || execution.Status != AgentToolExecutionStatus.Completed
            || !string.Equals(
                execution.OutcomeCode,
                "child-suspension-durable",
                StringComparison.Ordinal))
        {
            transaction.Rollback();
            return null;
        }

        Guid turnId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT TurnId FROM AgentTurnItems WHERE ToolExecutionId = $executionId AND Kind = 'ToolResult' LIMIT 1;";
            select.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString());
            if (select.ExecuteScalar() is not string value)
            {
                transaction.Rollback();
                return null;
            }
            turnId = Guid.Parse(value);
        }

        var now = new[] { DateTimeOffset.UtcNow, execution.UpdatedAtUtc.AddTicks(1) }.Max();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentTurnItems
                SET TextContent = $textContent,
                    ResultSummary = $resultSummary,
                    StructuredPayloadJson = $structuredPayloadJson,
                    SourcesJson = $sourcesJson,
                    WasTruncated = $wasTruncated,
                    IsError = $isError,
                    ErrorCode = $errorCode,
                    BackendId = $backendId,
                    PresentationPayloadJson = $presentationPayloadJson
                WHERE TurnId = $turnId
                  AND ToolExecutionId = $executionId
                  AND Kind = 'ToolResult';
                """;
            command.Parameters.AddWithValue("$textContent", (object?)result.Content ?? DBNull.Value);
            command.Parameters.AddWithValue("$resultSummary", (object?)result.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("$structuredPayloadJson", (object?)result.StructuredPayloadJson ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$sourcesJson",
                result.Sources is null ? DBNull.Value : JsonSerializer.Serialize(result.Sources));
            command.Parameters.AddWithValue("$wasTruncated", result.WasTruncated ? 1 : 0);
            command.Parameters.AddWithValue("$isError", result.IsError ? 1 : 0);
            command.Parameters.AddWithValue("$errorCode", (object?)result.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$backendId", (object?)result.BackendId ?? DBNull.Value);
            command.Parameters.AddWithValue("$presentationPayloadJson", (object?)result.PresentationPayloadJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var finalStatus = result.IsError
            ? AgentToolExecutionStatus.Failed
            : AgentToolExecutionStatus.Completed;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentToolExecutions
                SET Status = $status,
                    FinishedAtUtc = $finishedAtUtc,
                    UpdatedAtUtc = $updatedAtUtc,
                    OutcomeSummary = $outcomeSummary
                WHERE ExecutionId = $executionId
                  AND Status = 'Completed'
                  AND OutcomeCode = 'child-suspension-durable';
                """;
            command.Parameters.AddWithValue("$status", finalStatus.ToString());
            command.Parameters.AddWithValue("$finishedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue(
                "$outcomeSummary",
                (object?)Bound(result.Summary, MaxToolExecutionOutcomeSummaryLength) ?? DBNull.Value);
            command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE AgentTurns SET UpdatedAtUtc = $updatedAtUtc, ContentRevision = ContentRevision + 1 WHERE TurnId = $turnId;";
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        TouchSession(connection, key.SessionId, null, null, transaction);
        var turn = GetTurn(connection, turnId, transaction)
            ?? throw new InvalidOperationException("The replaced child tool-result turn could not be reloaded.");
        EnqueueRunLifecycleEvent(
            connection,
            transaction,
            AgentLifecycleEventKind.ToolResultRecorded,
            $"tool-execution:{execution.ExecutionId:N}:result-revision:{turn.ContentRevision}",
            key,
            triggerTurn: turn);
        transaction.Commit();
        return turn;
    }
}

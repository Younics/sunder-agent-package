using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal const int MaxToolExecutionOutcomeCodeLength = 128;
    internal const int MaxToolExecutionOutcomeSummaryLength = 2048;
    internal const int MaxToolExecutionOwnerPackageIdLength = 256;
    internal const int MaxToolExecutionSchemaIdLength = 512;
    internal const int MaxToolExecutionSchemaVersionLength = 128;

    private const string ToolExecutionColumns =
        "ExecutionId, SessionId, RunId, RunRevision, CallId, ToolId, InvocationFingerprint, IsReadOnly, Status, PreparedAtUtc, StartedAtUtc, FinishedAtUtc, UpdatedAtUtc, OutcomeCode, OutcomeSummary, OwnerPackageId, ToolSchemaId, ToolSchemaVersion, ExecutionTargetOwnerPackageId";

    internal IReadOnlyList<AgentToolExecutionPreparationResult>? TryPrepareToolExecutions(
        AgentDurableRunKey key,
        long expectedEpoch,
        IReadOnlyList<AgentToolExecutionPreparation> preparations)
    {
        ValidateToolExecutionPreparations(preparations);
        BeforeFencedTranscriptTransaction?.Invoke(AgentTranscriptMutationKind.ToolCall);
        var now = DateTimeOffset.UtcNow;
        var pending = preparations.Select(preparation => (
            Preparation: preparation,
            Execution: new AgentToolExecutionRecord(
                Guid.NewGuid(),
                key.SessionId,
                key.RunId,
                key.RunRevision,
                preparation.ToolCall.CallId,
                preparation.ToolCall.ToolId,
                preparation.InvocationFingerprint,
                preparation.IsReadOnly,
                AgentToolExecutionStatus.Prepared,
                now,
                StartedAtUtc: null,
                FinishedAtUtc: null,
                now,
                OutcomeCode: null,
                OutcomeSummary: null,
                preparation.OwnerPackageId,
                preparation.ToolSchemaId,
                preparation.ToolSchemaVersion,
                preparation.ExecutionTargetOwnerPackageId)))
            .ToArray();

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        if (!CanMutateTranscript(connection, transaction, key, expectedEpoch))
        {
            transaction.Rollback();
            return null;
        }

        foreach (var item in pending)
        {
            if (GetToolExecutionByCallId(
                    connection,
                    transaction,
                    key.RunId,
                    key.RunRevision,
                    item.Execution.CallId) is { } existing)
            {
                throw new InvalidOperationException(
                    existing.InvocationFingerprint == item.Execution.InvocationFingerprint
                    && string.Equals(existing.ToolId, item.Execution.ToolId, StringComparison.Ordinal)
                        ? $"Tool call '{item.Execution.CallId}' was already prepared for this run and will not be replayed."
                        : $"Tool call '{item.Execution.CallId}' conflicts with an existing durable invocation fingerprint.");
            }
        }

        var results = new List<AgentToolExecutionPreparationResult>(pending.Length);
        foreach (var item in pending)
        {
            InsertToolExecution(connection, transaction, item.Execution);
            var turn = CreateToolCallTurn(
                Guid.NewGuid(),
                key.SessionId,
                AgentMessageRole.Assistant,
                item.Execution.CallId,
                item.Execution.ToolId,
                item.Preparation.ToolCall.ArgumentsJson,
                now,
                now,
                item.Execution.ExecutionId,
                AgentToolExecutionStatus.Prepared,
                item.Execution.OwnerPackageId,
                item.Execution.ToolSchemaId,
                item.Execution.ToolSchemaVersion) with
            {
                RunId = key.RunId,
                RunRevision = key.RunRevision,
            };
            InsertTurn(connection, transaction, turn, runKey: key);
            results.Add(new AgentToolExecutionPreparationResult(item.Execution, turn));
        }

        TouchSession(connection, key.SessionId, null, null, transaction);
        transaction.Commit();
        return results;
    }

    internal AgentToolExecutionStartResult? TryStartToolExecution(
        AgentDurableRunKey key,
        long expectedEpoch,
        Guid executionId,
        string invocationFingerprint)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        if (!CanMutateTranscript(connection, transaction, key, expectedEpoch))
        {
            transaction.Rollback();
            return null;
        }

        var execution = GetToolExecution(connection, transaction, executionId);
        if (execution is null
            || execution.RunId != key.RunId
            || execution.SessionId != key.SessionId
            || execution.RunRevision != key.RunRevision
            || execution.Status != AgentToolExecutionStatus.Prepared
            || !string.Equals(
                execution.InvocationFingerprint,
                invocationFingerprint,
                StringComparison.Ordinal))
        {
            transaction.Rollback();
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentToolExecutions
                SET Status = 'Started',
                    StartedAtUtc = $startedAtUtc,
                    UpdatedAtUtc = $startedAtUtc
                WHERE ExecutionId = $executionId
                  AND Status = 'Prepared'
                  AND InvocationFingerprint = $invocationFingerprint;
                """;
            command.Parameters.AddWithValue("$startedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
            command.Parameters.AddWithValue("$invocationFingerprint", invocationFingerprint);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        UpdateToolCallTurnDetailRevision(connection, transaction, executionId, now);

        var updated = execution with
        {
            Status = AgentToolExecutionStatus.Started,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var toolCallTurn = GetToolCallTurn(connection, transaction, executionId)
            ?? throw new InvalidOperationException(
                $"Tool execution '{executionId}' has no durable tool-call transcript item.");
        transaction.Commit();
        return new AgentToolExecutionStartResult(updated, toolCallTurn);
    }

    internal AgentToolExecutionCompletionResult? TryCompleteToolExecution(
        AgentDurableRunKey key,
        long expectedEpoch,
        Guid executionId,
        AgentToolExecutionStatus status,
        AgentToolResult result,
        string outcomeCode,
        bool allowPreparedReadOnlyCompletion = false,
        AgentPendingPermissionRequestRecord? permissionRequest = null,
        AgentPendingPermissionStatus? permissionStatus = null)
    {
        if (status is not (AgentToolExecutionStatus.Completed or AgentToolExecutionStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

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

        var completion = TryCompleteToolExecution(
            connection,
            transaction,
            key,
            executionId,
            status,
            result,
            outcomeCode,
            allowPreparedReadOnlyCompletion,
            DateTimeOffset.UtcNow,
            permissionRequest,
            permissionStatus);
        if (completion is null)
        {
            transaction.Rollback();
            return null;
        }

        TouchSession(connection, key.SessionId, null, null, transaction);
        transaction.Commit();
        return completion;
    }

    internal AgentToolExecutionSuspensionResult? SuspendRunAndCompleteToolExecution(
        AgentDurableRunKey key,
        long expectedEpoch,
        AgentRunSuspension suspension,
        string? suspensionSummary,
        Guid executionId,
        AgentToolResult result,
        string outcomeCode,
        AgentPendingPermissionRequestRecord? permissionRequest = null)
    {
        BeforeFencedTranscriptTransaction?.Invoke(AgentTranscriptMutationKind.ToolResult);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        var suspended = SuspendRun(
            connection,
            transaction,
            key,
            expectedEpoch,
            suspension,
            suspensionSummary,
            Guid.NewGuid().ToString("N"));
        if (suspended is null)
        {
            transaction.Rollback();
            return null;
        }

        var completion = TryCompleteToolExecution(
            connection,
            transaction,
            key,
            executionId,
            AgentToolExecutionStatus.Completed,
            result,
            outcomeCode,
            allowPreparedReadOnlyCompletion: false,
            now: DateTimeOffset.UtcNow,
            permissionRequest: permissionRequest,
            permissionStatus: permissionRequest is null ? null : AgentPendingPermissionStatus.Executed);
        if (completion is null)
        {
            transaction.Rollback();
            return null;
        }

        TouchSession(connection, key.SessionId, null, null, transaction);
        transaction.Commit();
        return new AgentToolExecutionSuspensionResult(suspended, completion);
    }

    private AgentToolExecutionCompletionResult? TryCompleteToolExecution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        Guid executionId,
        AgentToolExecutionStatus status,
        AgentToolResult result,
        string outcomeCode,
        bool allowPreparedReadOnlyCompletion,
        DateTimeOffset now,
        AgentPendingPermissionRequestRecord? permissionRequest = null,
        AgentPendingPermissionStatus? permissionStatus = null)
    {
        if ((permissionRequest is null) != (permissionStatus is null)
            || permissionStatus is AgentPendingPermissionStatus.Pending
                or AgentPendingPermissionStatus.Claimed)
        {
            throw new ArgumentException("Linked permission completion requires matching terminal permission state.");
        }

        var execution = GetToolExecution(connection, transaction, executionId);
        if (execution is null
            || execution.RunId != key.RunId
            || execution.SessionId != key.SessionId
            || execution.RunRevision != key.RunRevision
            || HasToolResult(connection, transaction, executionId))
        {
            return null;
        }

        var validTransition = execution.Status switch
        {
            AgentToolExecutionStatus.Started => status is AgentToolExecutionStatus.Completed
                or AgentToolExecutionStatus.Failed
                or AgentToolExecutionStatus.Ambiguous,
            AgentToolExecutionStatus.Prepared => status == AgentToolExecutionStatus.Failed
                || status == AgentToolExecutionStatus.Completed
                && allowPreparedReadOnlyCompletion
                && execution.IsReadOnly,
            _ => false,
        };
        if (!validTransition)
        {
            return null;
        }

        var boundedCode = Bound(outcomeCode, MaxToolExecutionOutcomeCodeLength);
        var boundedSummary = Bound(result.Summary, MaxToolExecutionOutcomeSummaryLength);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentToolExecutions
                SET Status = $status,
                    FinishedAtUtc = $finishedAtUtc,
                    UpdatedAtUtc = $finishedAtUtc,
                    OutcomeCode = $outcomeCode,
                    OutcomeSummary = $outcomeSummary
                WHERE ExecutionId = $executionId
                  AND Status = $expectedStatus;
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$finishedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$outcomeCode", (object?)boundedCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$outcomeSummary", (object?)boundedSummary ?? DBNull.Value);
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
            command.Parameters.AddWithValue("$expectedStatus", execution.Status.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                return null;
            }
        }

        var resultTurn = CreateToolResultTurn(
            Guid.NewGuid(),
            key.SessionId,
            execution.CallId,
            execution.ToolId,
            GetToolArgumentsJson(connection, transaction, executionId),
            result.Content,
            result.Summary,
            result.StructuredPayloadJson,
            result.Sources is null ? null : JsonSerializer.Serialize(result.Sources),
            result.WasTruncated,
            result.IsError,
            result.ErrorCode,
            result.BackendId,
            result.PresentationPayloadJson,
            now,
            now,
            executionId,
            status,
            execution.OwnerPackageId,
            execution.ToolSchemaId,
            execution.ToolSchemaVersion) with
        {
            RunId = key.RunId,
            RunRevision = key.RunRevision,
        };
        InsertTurn(connection, transaction, resultTurn, runKey: key);
        if (permissionRequest is not null
            && !TryCompleteLinkedPermissionRequest(
                connection,
                transaction,
                execution,
                permissionRequest,
                permissionStatus!.Value,
                result.Summary,
                now))
        {
            return null;
        }

        EnqueueRunLifecycleEvent(
            connection,
            transaction,
            AgentLifecycleEventKind.ToolResultRecorded,
            $"tool-execution:{execution.ExecutionId:N}:terminal",
            key,
            triggerTurn: resultTurn);

        return new AgentToolExecutionCompletionResult(
            execution with
            {
                Status = status,
                FinishedAtUtc = now,
                UpdatedAtUtc = now,
                OutcomeCode = boundedCode,
                OutcomeSummary = boundedSummary,
            },
            resultTurn);
    }

    private static bool TryCompleteLinkedPermissionRequest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentToolExecutionRecord execution,
        AgentPendingPermissionRequestRecord request,
        AgentPendingPermissionStatus status,
        string? summary,
        DateTimeOffset now)
    {
        if (request.ToolExecutionId != execution.ExecutionId
            || request.SessionId != execution.SessionId
            || request.RunId != execution.RunId
            || request.RunRevision != execution.RunRevision
            || !string.Equals(request.CallId, execution.CallId, StringComparison.Ordinal)
            || !string.Equals(request.ToolId, execution.ToolId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.ClaimToken))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentPendingPermissionRequests
            SET Status = $status,
                DecidedAtUtc = $decidedAtUtc,
                DecisionSummary = $decisionSummary,
                ClaimLeaseExpiresAtUtc = NULL
            WHERE RequestId = $requestId
              AND SessionId = $sessionId
              AND RunId = $runId
              AND RunRevision = $runRevision
              AND CallId = $callId
              AND ToolId = $toolId
              AND ToolExecutionId = $executionId
              AND Status = 'Claimed'
              AND ClaimToken = $claimToken
              AND ContinuationConsumedAtUtc IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue(
            "$decisionSummary",
            Bound(summary, MaxToolExecutionOutcomeSummaryLength)
            ?? $"Approved tool '{execution.ToolId}' finished.");
        command.Parameters.AddWithValue("$requestId", request.RequestId);
        command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", request.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", request.RunRevision);
        command.Parameters.AddWithValue("$callId", request.CallId);
        command.Parameters.AddWithValue("$toolId", execution.ToolId);
        command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString());
        command.Parameters.AddWithValue("$claimToken", request.ClaimToken);
        return command.ExecuteNonQuery() == 1;
    }

    private static void InsertToolExecution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentToolExecutionRecord execution)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentToolExecutions (
                ExecutionId, SessionId, RunId, RunRevision, CallId, ToolId,
                InvocationFingerprint, IsReadOnly, Status, PreparedAtUtc,
                StartedAtUtc, FinishedAtUtc, UpdatedAtUtc, OutcomeCode, OutcomeSummary,
                OwnerPackageId, ToolSchemaId, ToolSchemaVersion, ExecutionTargetOwnerPackageId)
            VALUES (
                $executionId, $sessionId, $runId, $runRevision, $callId, $toolId,
                $invocationFingerprint, $isReadOnly, $status, $preparedAtUtc,
                NULL, NULL, $updatedAtUtc, NULL, NULL,
                $ownerPackageId, $toolSchemaId, $toolSchemaVersion, $executionTargetOwnerPackageId);
            """;
        command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString());
        command.Parameters.AddWithValue("$sessionId", execution.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", execution.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", execution.RunRevision);
        command.Parameters.AddWithValue("$callId", execution.CallId);
        command.Parameters.AddWithValue("$toolId", execution.ToolId);
        command.Parameters.AddWithValue("$invocationFingerprint", execution.InvocationFingerprint);
        command.Parameters.AddWithValue("$isReadOnly", execution.IsReadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$status", execution.Status.ToString());
        command.Parameters.AddWithValue("$preparedAtUtc", execution.PreparedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", execution.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$ownerPackageId", (object?)execution.OwnerPackageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolSchemaId", (object?)execution.ToolSchemaId ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolSchemaVersion", (object?)execution.ToolSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executionTargetOwnerPackageId",
            (object?)execution.ExecutionTargetOwnerPackageId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static AgentToolExecutionRecord? GetToolExecution(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid executionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ToolExecutionColumns} FROM AgentToolExecutions WHERE ExecutionId = $executionId;";
        command.Parameters.AddWithValue("$executionId", executionId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadToolExecution(reader) : null;
    }

    private static AgentToolExecutionRecord? GetToolExecutionByCallId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        long runRevision,
        string callId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ToolExecutionColumns} FROM AgentToolExecutions WHERE RunId = $runId AND RunRevision = $runRevision AND CallId = $callId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$runRevision", runRevision);
        command.Parameters.AddWithValue("$callId", callId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadToolExecution(reader) : null;
    }

    private static AgentTurnRecord? GetToolCallTurn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TurnId FROM AgentTurnItems WHERE ToolExecutionId = $executionId AND Kind = 'ToolCall' LIMIT 1;";
        command.Parameters.AddWithValue("$executionId", executionId.ToString());
        var turnId = command.ExecuteScalar() as string;
        return turnId is null ? null : GetTurn(connection, Guid.Parse(turnId), transaction);
    }

    private static string? GetToolArgumentsJson(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ArgumentsJson FROM AgentTurnItems WHERE ToolExecutionId = $executionId AND Kind = 'ToolCall' LIMIT 1;";
        command.Parameters.AddWithValue("$executionId", executionId.ToString());
        return command.ExecuteScalar() as string;
    }

    private static bool HasToolResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM AgentTurnItems WHERE ToolExecutionId = $executionId AND Kind = 'ToolResult' LIMIT 1;";
        command.Parameters.AddWithValue("$executionId", executionId.ToString());
        return command.ExecuteScalar() is not null;
    }

    private static AgentToolExecutionRecord ReadToolExecution(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7) != 0,
            Enum.Parse<AgentToolExecutionStatus>(reader.GetString(8), ignoreCase: true),
            DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));

    private static void ValidateToolExecutionPreparations(
        IReadOnlyList<AgentToolExecutionPreparation> preparations)
    {
        if (preparations.Count == 0)
        {
            throw new ArgumentException("At least one tool execution must be prepared.", nameof(preparations));
        }

        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preparation in preparations)
        {
            var call = preparation.ToolCall;
            if (string.IsNullOrWhiteSpace(call.CallId)
                || call.CallId.Length > 512
                || !string.Equals(call.CallId, call.CallId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Provider tool call ids must be nonblank, bounded, and free of surrounding whitespace.");
            }
            if (!callIds.Add(call.CallId))
            {
                throw new InvalidOperationException($"Provider tool call id '{call.CallId}' is duplicated in the same batch.");
            }
            if (string.IsNullOrWhiteSpace(call.ToolId)
                || call.ToolId.Length > 512
                || !string.Equals(call.ToolId, call.ToolId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Provider tool ids must be nonblank, bounded, and free of surrounding whitespace.");
            }
            if (!IsSha256(preparation.InvocationFingerprint))
            {
                throw new InvalidOperationException($"Tool call '{call.CallId}' has an invalid invocation fingerprint.");
            }
            ValidateOptionalProvenance(
                preparation.OwnerPackageId,
                MaxToolExecutionOwnerPackageIdLength,
                "owner package id");
            ValidateOptionalProvenance(
                preparation.ToolSchemaId,
                MaxToolExecutionSchemaIdLength,
                "tool schema id");
            ValidateOptionalProvenance(
                preparation.ToolSchemaVersion,
                MaxToolExecutionSchemaVersionLength,
                "tool schema version");
            ValidateOptionalProvenance(
                preparation.ExecutionTargetOwnerPackageId,
                MaxToolExecutionOwnerPackageIdLength,
                "execution-target owner package id");
            var expectedFingerprint = AgentToolInvocationFingerprint.Create(
                call.ToolId,
                call.ArgumentsJson);
            if (!string.Equals(
                    preparation.InvocationFingerprint,
                    expectedFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Tool call '{call.CallId}' does not match its invocation fingerprint.");
            }
        }
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Bound(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }

    private static void AddRunKeyParameters(SqliteCommand command, AgentDurableRunKey key)
    {
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
    }
}

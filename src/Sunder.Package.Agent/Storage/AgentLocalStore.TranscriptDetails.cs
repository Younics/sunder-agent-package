using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const int ToolDetailArgumentsMaximumBytes = 512 * 1024;
    private const int ToolDetailOutputMaximumBytes = 1024 * 1024;
    private const int ToolDetailOutputFallbackCharacters = 64 * 1024;
    private const int ToolDetailSummaryMaximumBytes = 64 * 1024;
    private const int ToolDetailJsonMaximumBytes = 512 * 1024;
    private const int ToolDetailPresentationMaximumBytes = 1024 * 1024;

    public AgentTranscriptToolDetailRecord? GetTranscriptToolDetail(AgentTranscriptToolDetailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty) return null;
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Guid? executionId = request.ToolExecutionId;
        if (executionId is null && request.ItemId is { } item)
        {
            using var resolve = connection.CreateCommand();
            resolve.Transaction = transaction;
            resolve.CommandText = """
                SELECT i.ToolExecutionId FROM AgentTurnItems i INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
                WHERE i.ItemId = $item AND t.SessionId = $session AND i.Kind IN ('ToolCall', 'ToolResult');
                """;
            resolve.Parameters.AddWithValue("$item", item.ToString());
            resolve.Parameters.AddWithValue("$session", request.SessionId.ToString());
            executionId = resolve.ExecuteScalar() is string value ? Guid.Parse(value) : null;
        }
        if (executionId is null) return null;

        ToolDetailMetadata metadata;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT e.CallId, e.ToolId, e.Status, e.DetailRevision, e.CallItemId, e.ResultItemId,
                       e.OwnerPackageId, e.ToolSchemaId, e.ToolSchemaVersion,
                       COALESCE(ct.RunId, rt.RunId), COALESCE(ct.RunRevision, rt.RunRevision), e.OutcomeSummary, e.OutcomeCode
                FROM AgentToolExecutions e
                LEFT JOIN AgentTurnItems c ON c.ItemId = e.CallItemId
                LEFT JOIN AgentTurns ct ON ct.TurnId = c.TurnId
                LEFT JOIN AgentTurnItems r ON r.ItemId = e.ResultItemId
                LEFT JOIN AgentTurns rt ON rt.TurnId = r.TurnId
                WHERE e.ExecutionId = $execution AND e.SessionId = $session
                  AND (ct.SessionId = $session OR rt.SessionId = $session);
                """;
            read.Parameters.AddWithValue("$execution", executionId.Value.ToString());
            read.Parameters.AddWithValue("$session", request.SessionId.ToString());
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return null;
            metadata = new(reader.GetString(0), reader.GetString(1), Enum.Parse<AgentToolExecutionStatus>(reader.GetString(2)), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12));
        }
        if (request.ItemId is { } requestedItem && requestedItem != metadata.CallItem && requestedItem != metadata.ResultItem
            || request.RunId is { } run && run != metadata.Run
            || request.RunRevision is { } revision && revision != metadata.RunRevision
            || !string.IsNullOrWhiteSpace(request.CallId) && request.CallId != metadata.CallId) return null;

        var payloads = ReadToolDetailPayloads(connection, transaction, request.SessionId, executionId.Value, metadata);
        var call = payloads.SingleOrDefault(payload => payload.Kind == AgentTurnItemKind.ToolCall);
        var result = payloads.SingleOrDefault(payload => payload.Kind == AgentTurnItemKind.ToolResult);
        if (call is null && result is null) return null;
        var detail = new AgentTranscriptToolDetailRecord(request.SessionId, executionId, metadata.CallId,
            metadata.CallItem, metadata.ResultItem, metadata.ToolId, call?.Arguments ?? result?.Arguments,
            result?.Output, result?.Summary ?? metadata.Summary, result?.Structured, result?.Sources, result?.Presentation,
            (call?.WasTruncated ?? false) || (result?.WasTruncated ?? false), result?.IsError
                ?? metadata.Status is AgentToolExecutionStatus.Failed or AgentToolExecutionStatus.Ambiguous,
            result is null ? metadata.Code : result.ErrorCode, result?.Backend, metadata.Status, metadata.Owner, metadata.Schema, metadata.SchemaVersion,
            metadata.Revision)
        {
            RunId = metadata.Run,
            RunRevision = metadata.RunRevision,
            WasTransportTruncated = payloads.Any(payload => payload.TransportTruncated),
        };
        transaction.Commit();
        return detail;
    }

    private static List<ToolDetailPayload> ReadToolDetailPayloads(SqliteConnection connection, SqliteTransaction transaction,
        Guid session, Guid execution, ToolDetailMetadata metadata)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.Kind,
                CASE WHEN length(CAST(i.ArgumentsJson AS BLOB)) <= $arguments THEN i.ArgumentsJson END,
                CASE WHEN length(CAST(i.TextContent AS BLOB)) <= $output THEN i.TextContent ELSE substr(i.TextContent, 1, $fallback) END,
                CASE WHEN length(CAST(i.ResultSummary AS BLOB)) <= $summary THEN i.ResultSummary ELSE substr(i.ResultSummary, 1, 2048) END,
                CASE WHEN length(CAST(i.StructuredPayloadJson AS BLOB)) <= $json THEN i.StructuredPayloadJson END,
                CASE WHEN length(CAST(i.SourcesJson AS BLOB)) <= $json THEN i.SourcesJson END,
                CASE WHEN length(CAST(i.PresentationPayloadJson AS BLOB)) <= $presentation THEN i.PresentationPayloadJson END,
                i.WasTruncated, i.IsError, substr(i.ErrorCode, 1, 256), substr(i.BackendId, 1, 512),
                CASE WHEN length(CAST(i.ArgumentsJson AS BLOB)) > $arguments
                    OR length(CAST(i.TextContent AS BLOB)) > $output
                    OR length(CAST(i.ResultSummary AS BLOB)) > $summary
                    OR length(CAST(i.StructuredPayloadJson AS BLOB)) > $json
                    OR length(CAST(i.SourcesJson AS BLOB)) > $json
                    OR length(CAST(i.PresentationPayloadJson AS BLOB)) > $presentation
                    OR length(i.ErrorCode) > 256 OR length(i.BackendId) > 512 THEN 1 ELSE 0 END
            FROM AgentTurnItems i INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
            WHERE i.ItemId IN ($call, $result) AND i.ToolExecutionId = $execution AND t.SessionId = $session;
            """;
        command.Parameters.AddWithValue("$call", metadata.CallItem?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$result", metadata.ResultItem?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$execution", execution.ToString());
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$arguments", ToolDetailArgumentsMaximumBytes);
        command.Parameters.AddWithValue("$output", ToolDetailOutputMaximumBytes);
        command.Parameters.AddWithValue("$fallback", ToolDetailOutputFallbackCharacters);
        command.Parameters.AddWithValue("$summary", ToolDetailSummaryMaximumBytes);
        command.Parameters.AddWithValue("$json", ToolDetailJsonMaximumBytes);
        command.Parameters.AddWithValue("$presentation", ToolDetailPresentationMaximumBytes);
        var payloads = new List<ToolDetailPayload>(2);
        using var reader = command.ExecuteReader();
        while (reader.Read()) payloads.Add(new(Enum.Parse<AgentTurnItemKind>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt64(7) != 0, reader.GetInt64(8) != 0, reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt64(11) != 0));
        return payloads;
    }

    private sealed record ToolDetailMetadata(string CallId, string ToolId, AgentToolExecutionStatus Status, long Revision,
        Guid? CallItem, Guid? ResultItem, string? Owner, string? Schema, string? SchemaVersion, Guid? Run, long? RunRevision,
        string? Summary, string? Code);
    private sealed record ToolDetailPayload(AgentTurnItemKind Kind, string? Arguments, string? Output, string? Summary,
        string? Structured, string? Sources, string? Presentation, bool WasTruncated, bool IsError, string? ErrorCode,
        string? Backend, bool TransportTruncated);
}

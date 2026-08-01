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

    public AgentTranscriptToolDetailRecord? GetTranscriptToolDetail(
        AgentTranscriptToolDetailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty
            || request.ToolExecutionId is null
               && request.ItemId is null
               && (request.RunId is null
                   || request.RunRevision is null
                   || string.IsNullOrWhiteSpace(request.CallId)))
        {
            return null;
        }

        using var connection = CreateConnection();
        connection.Open();
        var identity = ResolveToolDetailIdentity(connection, request);
        if (identity is null)
        {
            return null;
        }

        var correlated = ReadToolDetailItems(connection, identity);
        if (correlated.Count == 0
            || request.ItemId is { } requestedItemId
               && correlated.All(item => item.ItemId != requestedItemId))
        {
            return null;
        }

        var calls = correlated.Where(item => item.Kind == AgentTurnItemKind.ToolCall).ToArray();
        var results = correlated.Where(item => item.Kind == AgentTurnItemKind.ToolResult).ToArray();
        if (calls.Length > 1 || results.Length > 1)
        {
            return null;
        }

        var call = calls.SingleOrDefault();
        var result = results.SingleOrDefault();
        var authoritative = result ?? call;
        if (authoritative is null)
        {
            return null;
        }

        var status = identity.Status
                     ?? (result is null
                         ? AgentToolExecutionStatus.Started
                         : result.IsError
                             ? AgentToolExecutionStatus.Failed
                             : AgentToolExecutionStatus.Completed);
        var revision = new[]
            {
                call is null ? 0 : CreateProtocolTimestampRevision(call.UpdatedAtUtc),
                result is null ? 0 : CreateProtocolTimestampRevision(result.UpdatedAtUtc),
                identity.ExecutionUpdatedAtUtc is null ? 0 : CreateProtocolTimestampRevision(identity.ExecutionUpdatedAtUtc.Value),
            }
            .Max();
        return new AgentTranscriptToolDetailRecord(
            request.SessionId,
            authoritative.ToolExecutionId ?? identity.ToolExecutionId,
            authoritative.CallId ?? identity.CallId,
            call?.ItemId,
            result?.ItemId,
            authoritative.ToolId ?? call?.ToolId ?? "unknown_tool",
            call?.ArgumentsJson ?? result?.ArgumentsJson,
            result?.OutputText,
            result?.ResultSummary,
            result?.StructuredPayloadJson,
            result?.SourcesJson,
            result?.PresentationPayloadJson,
            (call?.WasTruncated ?? false) || (result?.WasTruncated ?? false),
            result?.IsError ?? false,
            result?.ErrorCode,
            result?.BackendId,
            status,
            identity.ToolOwnerPackageId,
            identity.ToolSchemaId,
            identity.ToolSchemaVersion,
            Math.Max(1, revision))
        {
            WasTransportTruncated = correlated.Any(item => item.WasTransportTruncated),
            RunId = authoritative.RunId ?? identity.RunId,
            RunRevision = authoritative.RunRevision ?? identity.RunRevision,
        };
    }

    private static ToolDetailIdentity? ResolveToolDetailIdentity(
        SqliteConnection connection,
        AgentTranscriptToolDetailRequest request)
    {
        var seed = request.ItemId is { } itemId
            ? ReadToolDetailSeed(connection, request.SessionId, "i.ItemId = $value", itemId.ToString())
            : request.ToolExecutionId is { } requestedExecutionId
                ? ReadToolDetailSeed(
                    connection,
                    request.SessionId,
                    "i.ToolExecutionId = $value",
                    requestedExecutionId.ToString())
                : null;
        if (request.ItemId is not null && seed is null
            || seed is not null
               && (request.ToolExecutionId is { } executionId && seed.ToolExecutionId != executionId
                   || request.RunId is { } requestedRunId && seed.RunId != requestedRunId
                   || request.RunRevision is { } requestedRunRevision
                      && seed.RunRevision != requestedRunRevision
                   || !string.IsNullOrWhiteSpace(request.CallId)
                      && !string.Equals(request.CallId, seed.CallId, StringComparison.Ordinal)))
        {
            return null;
        }

        var resolvedExecutionId = request.ToolExecutionId ?? seed?.ToolExecutionId;
        ToolExecutionDetailMetadata? execution = null;
        if (resolvedExecutionId is { } exactExecutionId)
        {
            using var executionCommand = connection.CreateCommand();
            executionCommand.CommandText = """
                SELECT SessionId, RunId, RunRevision, CallId, Status, UpdatedAtUtc,
                       OwnerPackageId, ToolSchemaId, ToolSchemaVersion
                FROM AgentToolExecutions
                WHERE ExecutionId = $executionId
                LIMIT 1;
                """;
            executionCommand.Parameters.AddWithValue("$executionId", exactExecutionId.ToString());
            using var reader = executionCommand.ExecuteReader();
            if (reader.Read())
            {
                execution = new ToolExecutionDetailMetadata(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    Enum.Parse<AgentToolExecutionStatus>(reader.GetString(4), ignoreCase: true),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8));
            }
        }

        var runId = request.RunId ?? seed?.RunId ?? execution?.RunId;
        var runRevision = request.RunRevision ?? seed?.RunRevision ?? execution?.RunRevision;
        var callId = request.CallId ?? seed?.CallId ?? execution?.CallId;
        if (execution is not null
            && (execution.SessionId != request.SessionId
                || runId != execution.RunId
                || runRevision != execution.RunRevision
                || !string.Equals(callId, execution.CallId, StringComparison.Ordinal)))
        {
            return null;
        }
        if (resolvedExecutionId is null
            && (runId is null || runRevision is null || string.IsNullOrWhiteSpace(callId))
            && seed is null)
        {
            return null;
        }

        return new ToolDetailIdentity(
            request.SessionId,
            resolvedExecutionId,
            request.ItemId,
            runId,
            runRevision,
            callId,
            execution?.Status,
            execution?.UpdatedAtUtc,
            execution?.ToolOwnerPackageId,
            execution?.ToolSchemaId,
            execution?.ToolSchemaVersion);
    }

    private static ToolDetailSeed? ReadToolDetailSeed(
        SqliteConnection connection,
        Guid sessionId,
        string predicate,
        string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.ItemId, i.Kind, i.ToolExecutionId, i.CallId, t.RunId, t.RunRevision
            FROM AgentTurnItems i
            INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
            WHERE t.SessionId = $sessionId
              AND {predicate}
              AND i.Kind IN ('ToolCall', 'ToolResult')
            ORDER BY t.CreatedAtUtc COLLATE BINARY, t.TurnId COLLATE BINARY, i.SequenceNumber
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ToolDetailSeed(
                Guid.Parse(reader.GetString(0)),
                Enum.Parse<AgentTurnItemKind>(reader.GetString(1), ignoreCase: true),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5))
            : null;
    }

    private static IReadOnlyList<ToolDetailItem> ReadToolDetailItems(
        SqliteConnection connection,
        ToolDetailIdentity identity)
    {
        using var command = connection.CreateCommand();
        var predicates = new List<string>();
        if (identity.ToolExecutionId is { } executionId)
        {
            predicates.Add("i.ToolExecutionId = $executionId");
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
        }
        if (identity.RunId is { } runId
            && identity.RunRevision is { } runRevision
            && !string.IsNullOrWhiteSpace(identity.CallId))
        {
            predicates.Add("(t.RunId = $runId AND t.RunRevision = $runRevision AND i.CallId = $callId" +
                           (identity.ToolExecutionId is null ? ")" : " AND i.ToolExecutionId IS NULL)"));
            command.Parameters.AddWithValue("$runId", runId.ToString());
            command.Parameters.AddWithValue("$runRevision", runRevision);
            command.Parameters.AddWithValue("$callId", identity.CallId);
        }
        if (predicates.Count == 0 && identity.ItemId is { } itemId)
        {
            predicates.Add("i.ItemId = $itemId");
            command.Parameters.AddWithValue("$itemId", itemId.ToString());
        }
        if (predicates.Count == 0)
        {
            return [];
        }

        command.CommandText = $"""
            SELECT i.ItemId,
                   i.Kind,
                   substr(i.CallId, 1, 1024),
                   substr(i.ToolId, 1, 1024),
                   CASE WHEN length(CAST(i.ArgumentsJson AS BLOB)) <= $argumentsMaximumBytes
                        THEN i.ArgumentsJson ELSE NULL END,
                   CASE WHEN length(CAST(i.TextContent AS BLOB)) <= $outputMaximumBytes
                        THEN i.TextContent ELSE substr(i.TextContent, 1, $outputFallbackCharacters) END,
                   CASE WHEN length(CAST(i.ResultSummary AS BLOB)) <= $summaryMaximumBytes
                        THEN i.ResultSummary ELSE substr(i.ResultSummary, 1, 2048) END,
                   CASE WHEN length(CAST(i.StructuredPayloadJson AS BLOB)) <= $jsonMaximumBytes
                        THEN i.StructuredPayloadJson ELSE NULL END,
                   CASE WHEN length(CAST(i.SourcesJson AS BLOB)) <= $jsonMaximumBytes
                        THEN i.SourcesJson ELSE NULL END,
                   CASE WHEN length(CAST(i.PresentationPayloadJson AS BLOB)) <= $presentationMaximumBytes
                        THEN i.PresentationPayloadJson ELSE NULL END,
                   i.WasTruncated,
                   i.IsError,
                   substr(i.ErrorCode, 1, 256),
                   substr(i.BackendId, 1, 512),
                   i.ToolExecutionId,
                   t.UpdatedAtUtc,
                   t.RunId,
                   t.RunRevision,
                   CASE WHEN length(CAST(i.ArgumentsJson AS BLOB)) > $argumentsMaximumBytes
                          OR length(CAST(i.TextContent AS BLOB)) > $outputMaximumBytes
                          OR length(CAST(i.ResultSummary AS BLOB)) > $summaryMaximumBytes
                          OR length(CAST(i.StructuredPayloadJson AS BLOB)) > $jsonMaximumBytes
                          OR length(CAST(i.SourcesJson AS BLOB)) > $jsonMaximumBytes
                          OR length(CAST(i.PresentationPayloadJson AS BLOB)) > $presentationMaximumBytes
                          OR length(i.ErrorCode) > 256
                          OR length(i.BackendId) > 512
                        THEN 1 ELSE 0 END
            FROM AgentTurnItems i
            INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
            WHERE t.SessionId = $sessionId
              AND i.Kind IN ('ToolCall', 'ToolResult')
              AND ({string.Join(" OR ", predicates)})
            ORDER BY t.CreatedAtUtc COLLATE BINARY,
                     t.TurnId COLLATE BINARY,
                     i.SequenceNumber
            LIMIT 3;
            """;
        command.Parameters.AddWithValue("$sessionId", identity.SessionId.ToString());
        command.Parameters.AddWithValue("$argumentsMaximumBytes", ToolDetailArgumentsMaximumBytes);
        command.Parameters.AddWithValue("$outputMaximumBytes", ToolDetailOutputMaximumBytes);
        command.Parameters.AddWithValue("$outputFallbackCharacters", ToolDetailOutputFallbackCharacters);
        command.Parameters.AddWithValue("$summaryMaximumBytes", ToolDetailSummaryMaximumBytes);
        command.Parameters.AddWithValue("$jsonMaximumBytes", ToolDetailJsonMaximumBytes);
        command.Parameters.AddWithValue("$presentationMaximumBytes", ToolDetailPresentationMaximumBytes);
        using var reader = command.ExecuteReader();
        var items = new List<ToolDetailItem>(2);
        while (reader.Read())
        {
            items.Add(new ToolDetailItem(
                Guid.Parse(reader.GetString(0)),
                Enum.Parse<AgentTurnItemKind>(reader.GetString(1), ignoreCase: true),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10) != 0,
                reader.GetInt64(11) != 0,
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
                DateTimeOffset.Parse(reader.GetString(15)),
                reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetInt64(17),
                reader.GetInt64(18) != 0));
        }
        return items;
    }

    private sealed record ToolDetailSeed(
        Guid ItemId,
        AgentTurnItemKind Kind,
        Guid? ToolExecutionId,
        string? CallId,
        Guid? RunId,
        long? RunRevision);

    private sealed record ToolExecutionDetailMetadata(
        Guid SessionId,
        Guid RunId,
        long RunRevision,
        string CallId,
        AgentToolExecutionStatus Status,
        DateTimeOffset UpdatedAtUtc,
        string? ToolOwnerPackageId,
        string? ToolSchemaId,
        string? ToolSchemaVersion);

    private sealed record ToolDetailIdentity(
        Guid SessionId,
        Guid? ToolExecutionId,
        Guid? ItemId,
        Guid? RunId,
        long? RunRevision,
        string? CallId,
        AgentToolExecutionStatus? Status,
        DateTimeOffset? ExecutionUpdatedAtUtc,
        string? ToolOwnerPackageId,
        string? ToolSchemaId,
        string? ToolSchemaVersion);

    private sealed record ToolDetailItem(
        Guid ItemId,
        AgentTurnItemKind Kind,
        string? CallId,
        string? ToolId,
        string? ArgumentsJson,
        string? OutputText,
        string? ResultSummary,
        string? StructuredPayloadJson,
        string? SourcesJson,
        string? PresentationPayloadJson,
        bool WasTruncated,
        bool IsError,
        string? ErrorCode,
        string? BackendId,
        Guid? ToolExecutionId,
        DateTimeOffset UpdatedAtUtc,
        Guid? RunId,
        long? RunRevision,
        bool WasTransportTruncated);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const string LegacyToolExecutionIdentityBackfillV21 = """
        identity-v1 = sha256("sunder-tool-execution-v21" + run-id + run-revision + call-id + pair-ordinal)
        runless-identity-v1 = sha256("sunder-tool-execution-v21-runless" + session-id + call-id + pair-ordinal)
        process legacy ToolCall and ToolResult items in deterministic transcript chronology
        pair each result with the nearest compatible unmatched preceding call only within one RunId, RunRevision, and CallId
        pair rows without a complete run identity only within one SessionId and CallId
        require matching tool identities when both are available and preserve compatible existing execution identities
        assign pair ordinals when calls and true result orphans are encountered so reused identifiers remain sequential
        preserve existing ToolExecutionId values and fill only null counterparts

        CREATE INDEX IX_AgentTurns_RunIdentity
            ON AgentTurns (SessionId, RunId, RunRevision)
            WHERE RunId IS NOT NULL AND RunRevision IS NOT NULL;
        CREATE INDEX IX_AgentTurnItems_CallId
            ON AgentTurnItems (CallId)
            WHERE CallId IS NOT NULL AND Kind IN ('ToolCall', 'ToolResult');
        """;

    private static void ApplyLegacyToolExecutionIdentityBackfill(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var items = new List<LegacyToolExecutionItem>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT i.ItemId,
                       i.Kind,
                       i.ToolExecutionId,
                       t.SessionId,
                       t.RunId,
                       t.RunRevision,
                       i.CallId,
                       i.ToolId
                FROM AgentTurnItems i
                INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
                WHERE i.CallId IS NOT NULL
                  AND trim(i.CallId) <> ''
                  AND i.Kind IN ('ToolCall', 'ToolResult')
                ORDER BY t.SessionId COLLATE BINARY,
                         CASE WHEN t.RunId IS NOT NULL AND t.RunRevision IS NOT NULL THEN 0 ELSE 1 END,
                         CASE WHEN t.RunId IS NOT NULL AND t.RunRevision IS NOT NULL
                              THEN t.RunId ELSE NULL END COLLATE BINARY,
                         CASE WHEN t.RunId IS NOT NULL AND t.RunRevision IS NOT NULL
                              THEN t.RunRevision ELSE NULL END,
                         i.CallId COLLATE BINARY,
                         t.CreatedAtUtc COLLATE BINARY,
                         t.TurnId COLLATE BINARY,
                         i.SequenceNumber,
                         i.ItemId COLLATE BINARY;
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var hasRunIdentity = !reader.IsDBNull(4) && !reader.IsDBNull(5);
                items.Add(new LegacyToolExecutionItem(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3)),
                    hasRunIdentity ? Guid.Parse(reader.GetString(4)) : null,
                    hasRunIdentity ? reader.GetInt64(5) : null,
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        foreach (var group in items.GroupBy(
                     static item => new LegacyToolExecutionGroupKey(
                         item.SessionId,
                         item.RunId,
                         item.RunRevision,
                         item.CallId)))
        {
            var groupItems = group.ToArray();
            var callExecutionIds = groupItems
                .Where(static item => item.Kind == "ToolCall")
                .Select(static item => item.ToolExecutionId)
                .OfType<Guid>()
                .ToHashSet();
            var resultExecutionIds = groupItems
                .Where(static item => item.Kind == "ToolResult")
                .Select(static item => item.ToolExecutionId)
                .OfType<Guid>()
                .ToHashSet();
            var pairs = new List<LegacyToolExecutionPair>();
            var unmatchedCallPairIndexes = new List<int>();
            foreach (var item in groupItems)
            {
                if (item.Kind == "ToolCall")
                {
                    unmatchedCallPairIndexes.Add(pairs.Count);
                    pairs.Add(new LegacyToolExecutionPair(item, Result: null));
                    continue;
                }

                var unmatchedIndex = unmatchedCallPairIndexes.FindLastIndex(pairIndex =>
                    CanPairLegacyToolExecutionItems(
                        pairs[pairIndex].Call!,
                        item,
                        callExecutionIds,
                        resultExecutionIds));
                if (unmatchedIndex < 0)
                {
                    pairs.Add(new LegacyToolExecutionPair(Call: null, item));
                    continue;
                }

                var matchedPairIndex = unmatchedCallPairIndexes[unmatchedIndex];
                pairs[matchedPairIndex] = pairs[matchedPairIndex] with { Result = item };
                unmatchedCallPairIndexes.RemoveAt(unmatchedIndex);
            }

            for (var pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                var pair = pairs[pairIndex];
                var executionId = ResolveLegacyExecutionId(
                    group.Key,
                    pairIndex,
                    pair.Call,
                    pair.Result,
                    callExecutionIds,
                    resultExecutionIds);
                if (executionId is null)
                {
                    continue;
                }

                BackfillLegacyToolExecutionId(connection, transaction, pair.Call, executionId.Value);
                BackfillLegacyToolExecutionId(connection, transaction, pair.Result, executionId.Value);
            }
        }

        using var indexes = connection.CreateCommand();
        indexes.Transaction = transaction;
        indexes.CommandText = """
            CREATE INDEX IX_AgentTurns_RunIdentity
                ON AgentTurns (SessionId, RunId, RunRevision)
                WHERE RunId IS NOT NULL AND RunRevision IS NOT NULL;
            CREATE INDEX IX_AgentTurnItems_CallId
                ON AgentTurnItems (CallId)
                WHERE CallId IS NOT NULL AND Kind IN ('ToolCall', 'ToolResult');
            """;
        indexes.ExecuteNonQuery();
    }

    private static bool CanPairLegacyToolExecutionItems(
        LegacyToolExecutionItem call,
        LegacyToolExecutionItem result,
        IReadOnlySet<Guid> callExecutionIds,
        IReadOnlySet<Guid> resultExecutionIds)
    {
        if (!string.IsNullOrWhiteSpace(call.ToolId)
            && !string.IsNullOrWhiteSpace(result.ToolId)
            && !string.Equals(call.ToolId, result.ToolId, StringComparison.Ordinal))
        {
            return false;
        }

        if (call.ToolExecutionId is { } callExecutionId
            && result.ToolExecutionId is { } resultExecutionId)
        {
            return callExecutionId == resultExecutionId;
        }
        if (call.ToolExecutionId is { } existingCallExecutionId)
        {
            return !resultExecutionIds.Contains(existingCallExecutionId);
        }
        if (result.ToolExecutionId is { } existingResultExecutionId)
        {
            return !callExecutionIds.Contains(existingResultExecutionId);
        }

        return true;
    }

    private static Guid? ResolveLegacyExecutionId(
        LegacyToolExecutionGroupKey key,
        int pairIndex,
        LegacyToolExecutionItem? call,
        LegacyToolExecutionItem? result,
        IReadOnlySet<Guid> callExecutionIds,
        IReadOnlySet<Guid> resultExecutionIds)
    {
        if (call?.ToolExecutionId is { } callExecutionId
            && result?.ToolExecutionId is { } resultExecutionId)
        {
            return callExecutionId == resultExecutionId ? callExecutionId : null;
        }
        if (call?.ToolExecutionId is { } existingCallExecutionId)
        {
            return resultExecutionIds.Contains(existingCallExecutionId)
                ? null
                : existingCallExecutionId;
        }
        if (result?.ToolExecutionId is { } existingResultExecutionId)
        {
            return callExecutionIds.Contains(existingResultExecutionId)
                ? null
                : existingResultExecutionId;
        }

        var material = key.RunId is { } runId && key.RunRevision is { } runRevision
            ? string.Join(
                '\n',
                "sunder-tool-execution-v21",
                runId.ToString("D"),
                runRevision.ToString(CultureInfo.InvariantCulture),
                pairIndex.ToString(CultureInfo.InvariantCulture),
                key.CallId.Length.ToString(CultureInfo.InvariantCulture),
                key.CallId)
            : string.Join(
                '\n',
                "sunder-tool-execution-v21-runless",
                key.SessionId.ToString("D"),
                pairIndex.ToString(CultureInfo.InvariantCulture),
                key.CallId.Length.ToString(CultureInfo.InvariantCulture),
                key.CallId);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
    }

    private static void BackfillLegacyToolExecutionId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyToolExecutionItem? item,
        Guid executionId)
    {
        if (item is null || item.ToolExecutionId is not null)
        {
            return;
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AgentTurnItems
            SET ToolExecutionId = $executionId
            WHERE ItemId = $itemId
              AND ToolExecutionId IS NULL;
            """;
        update.Parameters.AddWithValue("$executionId", executionId.ToString());
        update.Parameters.AddWithValue("$itemId", item.ItemId.ToString());
        update.ExecuteNonQuery();
    }

    private sealed record LegacyToolExecutionItem(
        Guid ItemId,
        string Kind,
        Guid? ToolExecutionId,
        Guid SessionId,
        Guid? RunId,
        long? RunRevision,
        string CallId,
        string? ToolId);

    private sealed record LegacyToolExecutionPair(
        LegacyToolExecutionItem? Call,
        LegacyToolExecutionItem? Result);

    private sealed record LegacyToolExecutionGroupKey(
        Guid SessionId,
        Guid? RunId,
        long? RunRevision,
        string CallId);
}

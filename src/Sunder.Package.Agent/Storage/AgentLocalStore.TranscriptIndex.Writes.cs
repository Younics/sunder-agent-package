using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static long AllocateTurnSequence(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE AgentSessions SET NextTurnSequence = NextTurnSequence + 1 WHERE SessionId = $session RETURNING NextTurnSequence - 1;";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        return command.ExecuteScalar() is long sequence ? sequence
            : throw new InvalidOperationException("A transcript turn requires its owning session.");
    }

    // Recording helpers and run-owned execution share one persisted identity.
    // Readers never infer a pair from text, timestamps or an unscoped provider id.
    private static AgentTurnRecord NormalizeToolIdentities(SqliteConnection connection, SqliteTransaction transaction, AgentTurnRecord turn)
    {
        return turn with { Items = turn.Items.Select(item =>
        {
            if (item.Kind is not (AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult)) return item;
            var execution = item.ToolExecutionId is { } id ? GetToolExecution(connection, transaction, id)
                : turn.RunId is { } run && turn.RunRevision is { } revision
                    ? GetToolExecutionByCallId(connection, transaction, run, revision, item.CallId ?? string.Empty)
                    : item.Kind == AgentTurnItemKind.ToolResult
                        ? ResolveRecordingExecution(connection, transaction, turn.SessionId, item.CallId ?? string.Empty) : null;
            if (execution is null)
            {
                var executionId = item.ToolExecutionId ?? Guid.NewGuid();
                execution = new AgentToolExecutionRecord(executionId, turn.SessionId,
                    turn.RunId ?? executionId, turn.RunRevision ?? 0,
                    item.CallId ?? throw new InvalidOperationException("A tool item requires a provider call id."),
                    item.ToolId ?? throw new InvalidOperationException("A tool item requires a tool id."),
                    AgentToolInvocationFingerprint.Create(item.ToolId!, item.ArgumentsJson ?? "{}"),
                    false, item.ToolExecutionStatus ?? (item.Kind == AgentTurnItemKind.ToolCall
                        ? AgentToolExecutionStatus.Started : item.IsError ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed),
                    turn.CreatedAtUtc, turn.CreatedAtUtc, null, turn.UpdatedAtUtc, null, item.ResultSummary,
                    item.ToolOwnerPackageId, item.ToolSchemaId, item.ToolSchemaVersion);
                InsertToolExecution(connection, transaction, execution);
            }
            if (execution.SessionId != turn.SessionId || execution.CallId != item.CallId || execution.ToolId != item.ToolId
                || turn.RunId is { } owner && (owner != execution.RunId || turn.RunRevision != execution.RunRevision))
                throw new InvalidOperationException("A transcript item cannot retarget a durable tool execution.");
            return item with
            {
                ToolExecutionId = execution.ExecutionId,
                ToolExecutionStatus = item.Kind == AgentTurnItemKind.ToolResult
                    && execution.Status is AgentToolExecutionStatus.Prepared or AgentToolExecutionStatus.Started
                        ? item.IsError ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed
                        : execution.Status,
            };
        }).ToArray() };
    }

    // The unowned recording API admits a result against its one still-open
    // recording call. Persist that identity now; readers never repeat this work.
    // Ambiguous/reused outstanding provider ids are rejected rather than guessed.
    private static AgentToolExecutionRecord? ResolveRecordingExecution(SqliteConnection connection, SqliteTransaction transaction,
        Guid session, string call)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ExecutionId FROM AgentToolExecutions WHERE SessionId = $session AND CallId = $call
                AND RunRevision = 0 AND CallItemId IS NOT NULL AND ResultItemId IS NULL LIMIT 2;
            """;
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$call", call);
        Guid? execution = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read()) execution = Guid.Parse(reader.GetString(0));
            if (reader.Read()) throw new InvalidOperationException("The recording call id identifies more than one outstanding invocation.");
        }
        return execution is { } id ? GetToolExecution(connection, transaction, id) : null;
    }

    private static int InvocationState(AgentToolExecutionStatus status) => status switch
    {
        AgentToolExecutionStatus.Prepared or AgentToolExecutionStatus.Started => 0,
        AgentToolExecutionStatus.Completed => 1,
        _ => 2,
    };

    private static void IndexInsertedTurn(SqliteConnection connection, SqliteTransaction transaction, AgentTurnRecord turn)
    {
        if (turn.Kind is not (AgentTurnKind.ToolCall or AgentTurnKind.ToolResult))
        {
            IndexMessage(connection, transaction, turn);
            return;
        }
        foreach (var item in turn.Items.Where(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult))
        {
            var executionId = item.ToolExecutionId!.Value;
            var execution = GetToolExecution(connection, transaction, executionId)!;
            var isResult = item.Kind == AgentTurnItemKind.ToolResult;
            var state = execution.Status;
            if (isResult && state is AgentToolExecutionStatus.Prepared or AgentToolExecutionStatus.Started)
                state = item.IsError ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = isResult ? """
                    UPDATE AgentToolExecutions SET ResultItemId = $item, Status = $status,
                        HeaderRevision = HeaderRevision + 1, DetailRevision = DetailRevision + 1,
                        HasDetails = MAX(HasDetails, $details), HeaderHint = COALESCE(HeaderHint, $hint),
                        OutcomeSummary = COALESCE(OutcomeSummary, $summary), UpdatedAtUtc = $updated
                    WHERE ExecutionId = $execution AND ResultItemId IS NULL;
                    """ : """
                    UPDATE AgentToolExecutions SET CallItemId = $item, HasDetails = MAX(HasDetails, $details),
                        HeaderHint = $hint WHERE ExecutionId = $execution AND CallItemId IS NULL;
                    """;
                command.Parameters.AddWithValue("$execution", executionId.ToString());
                command.Parameters.AddWithValue("$item", item.ItemId.ToString());
                command.Parameters.AddWithValue("$status", state.ToString());
                command.Parameters.AddWithValue("$summary", (object?)Bound(item.ResultSummary, MaxToolExecutionOutcomeSummaryLength) ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", turn.UpdatedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$details", HasInvocationDetails(item) ? 1 : 0);
                command.Parameters.AddWithValue("$hint", (object?)FileToolHeaderHint.FromArguments(item.ToolId, item.ArgumentsJson) ?? DBNull.Value);
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("A tool execution already has this transcript receipt kind.");
            }

            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT EntryId FROM AgentTranscriptEntries WHERE ExecutionId = $execution;";
            existing.Parameters.AddWithValue("$execution", executionId.ToString());
            if (existing.ExecuteScalar() is not string)
            {
                InsertTranscriptEntry(connection, transaction, turn, item.ItemId, item.SequenceNumber, executionId, InvocationState(state));
                AssignInvocationGroup(connection, transaction, turn, item.ItemId, item.SequenceNumber, item.ToolId!, InvocationState(state));
            }
            else
            {
                UpdateInvocationIndexState(connection, transaction, executionId, InvocationState(state));
            }
        }
    }

    private static bool HasInvocationDetails(AgentTurnItemRecord item)
        => item.IsError || item.ToolExecutionStatus == AgentToolExecutionStatus.Ambiguous
            || !string.IsNullOrWhiteSpace(item.ArgumentsJson) && item.ArgumentsJson.Trim() != "{}"
            || new[] { item.TextContent, item.ResultSummary, item.StructuredPayloadJson, item.SourcesJson,
                item.PresentationPayloadJson, item.ErrorCode, item.BackendId }.Any(value => !string.IsNullOrWhiteSpace(value));

    private static void InsertTranscriptEntry(SqliteConnection connection, SqliteTransaction transaction,
        AgentTurnRecord turn, Guid entryId, int itemSequence, Guid? executionId, int? state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentTranscriptEntries (EntryId, SessionId, TurnId, TurnSequence, ItemSequence, ExecutionId, State, CompactVisible)
            VALUES ($entry, $session, $turn, $sequence, $itemSequence, $execution, $state, $visible);
            """;
        command.Parameters.AddWithValue("$entry", entryId.ToString());
        command.Parameters.AddWithValue("$session", turn.SessionId.ToString());
        command.Parameters.AddWithValue("$turn", turn.TurnId.ToString());
        command.Parameters.AddWithValue("$sequence", turn.TurnSequence);
        command.Parameters.AddWithValue("$itemSequence", itemSequence);
        command.Parameters.AddWithValue("$execution", executionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("$visible", executionId is null ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void AssignInvocationGroup(SqliteConnection connection, SqliteTransaction transaction,
        AgentTurnRecord turn, Guid entryId, int itemSequence, string toolId, int state)
    {
        string? groupId = null;
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = """
                SELECT p.GroupId, t.RunId, t.RunRevision FROM AgentTranscriptEntries p
                INNER JOIN AgentTurns t ON t.TurnId = p.TurnId
                WHERE p.SessionId = $session AND (p.TurnSequence, p.ItemSequence) < ($sequence, $item)
                ORDER BY p.TurnSequence DESC, p.ItemSequence DESC LIMIT 1;
                """;
            previous.Parameters.AddWithValue("$session", turn.SessionId.ToString());
            previous.Parameters.AddWithValue("$sequence", turn.TurnSequence);
            previous.Parameters.AddWithValue("$item", itemSequence);
            using var reader = previous.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0)
                && (reader.IsDBNull(1) ? null : reader.GetString(1)) == turn.RunId?.ToString()
                && (reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2)) == turn.RunRevision)
                groupId = reader.GetString(0);
        }
        var first = groupId is null;
        groupId ??= entryId.ToString();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentTranscriptGroups (GroupId, SessionId, RunId, RunRevision, FirstEntryId, LastEntryId,
                Revision, RunningCount, CompletedCount, FailedCount)
            VALUES ($group, $session, $run, $revision, $entry, $entry, 1, $running, $completed, $failed)
            ON CONFLICT (GroupId) DO UPDATE SET LastEntryId = $entry, Revision = Revision + 1,
                RunningCount = RunningCount + $running, CompletedCount = CompletedCount + $completed, FailedCount = FailedCount + $failed;
            UPDATE AgentTranscriptEntries SET GroupId = $group, CompactVisible = $visible WHERE EntryId = $entry;
            INSERT INTO AgentTranscriptGroupTools (GroupId, ToolId, FirstTurnSequence, FirstItemSequence, Count)
            VALUES ($group, $tool, $sequence, $item, 1)
            ON CONFLICT (GroupId, ToolId) DO UPDATE SET Count = Count + 1;
            """;
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$session", turn.SessionId.ToString());
        command.Parameters.AddWithValue("$run", turn.RunId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)turn.RunRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry", entryId.ToString());
        command.Parameters.AddWithValue("$running", state == 0 ? 1 : 0);
        command.Parameters.AddWithValue("$completed", state == 1 ? 1 : 0);
        command.Parameters.AddWithValue("$failed", state == 2 ? 1 : 0);
        command.Parameters.AddWithValue("$visible", first ? 1 : 0);
        command.Parameters.AddWithValue("$tool", toolId);
        command.Parameters.AddWithValue("$sequence", turn.TurnSequence);
        command.Parameters.AddWithValue("$item", itemSequence);
        command.ExecuteNonQuery();
    }

    private static void UpdateInvocationIndexState(SqliteConnection connection, SqliteTransaction transaction, Guid executionId, int state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentTranscriptGroups SET Revision = Revision + 1,
                RunningCount = RunningCount + ($state = 0) - (SELECT State = 0 FROM AgentTranscriptEntries WHERE ExecutionId = $execution),
                CompletedCount = CompletedCount + ($state = 1) - (SELECT State = 1 FROM AgentTranscriptEntries WHERE ExecutionId = $execution),
                FailedCount = FailedCount + ($state = 2) - (SELECT State = 2 FROM AgentTranscriptEntries WHERE ExecutionId = $execution)
            WHERE GroupId = (SELECT GroupId FROM AgentTranscriptEntries WHERE ExecutionId = $execution);
            UPDATE AgentTranscriptEntries SET State = $state WHERE ExecutionId = $execution;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString());
        command.Parameters.AddWithValue("$state", state);
        command.ExecuteNonQuery();
    }

    private static bool IndexMessage(SqliteConnection connection, SqliteTransaction transaction, AgentTurnRecord turn, bool invalidateContext = true)
    {
        var visible = turn.Items.Any(item => item.Kind == AgentTurnItemKind.Attachment
            || item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent));
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT 1 FROM AgentTranscriptEntries WHERE EntryId = $entry;";
        find.Parameters.AddWithValue("$entry", turn.TurnId.ToString());
        var exists = find.ExecuteScalar() is not null;
        if (visible == exists) return false;
        if (visible) InsertTranscriptEntry(connection, transaction, turn, turn.TurnId, -1, null, null);
        else
        {
            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM AgentTranscriptEntries WHERE EntryId = $entry;";
            remove.Parameters.AddWithValue("$entry", turn.TurnId.ToString());
            remove.ExecuteNonQuery();
        }
        using var later = connection.CreateCommand();
        later.Transaction = transaction;
        later.CommandText = "SELECT 1 FROM AgentTranscriptEntries WHERE SessionId = $session AND TurnSequence > $sequence LIMIT 1;";
        later.Parameters.AddWithValue("$session", turn.SessionId.ToString());
        later.Parameters.AddWithValue("$sequence", turn.TurnSequence);
        if (later.ExecuteScalar() is null) return false;
        RebuildTranscriptGroups(connection, transaction, turn.SessionId, invalidateContext);
        return true;
    }

    // Structural edits/rollback are rare. Repair their group boundaries once at
    // write time, never during a page read or an ordinary streamed append.
    private static void RebuildTranscriptGroups(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, bool invalidateContext = true)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM AgentTranscriptGroupTools WHERE GroupId IN (SELECT GroupId FROM AgentTranscriptGroups WHERE SessionId = $session);
                DELETE FROM AgentTranscriptGroups WHERE SessionId = $session;
                UPDATE AgentTranscriptEntries SET GroupId = NULL, CompactVisible = (ExecutionId IS NULL) WHERE SessionId = $session;
                """;
            clear.Parameters.AddWithValue("$session", sessionId.ToString());
            clear.ExecuteNonQuery();
        }
        var entries = new List<(Guid Entry, Guid Turn, long Sequence, int Item, string Tool, int State, Guid? Run, long? Revision)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT p.EntryId, p.TurnId, p.TurnSequence, p.ItemSequence, e.ToolId, p.State, t.RunId, t.RunRevision
                FROM AgentTranscriptEntries p INNER JOIN AgentToolExecutions e ON e.ExecutionId = p.ExecutionId
                INNER JOIN AgentTurns t ON t.TurnId = p.TurnId WHERE p.SessionId = $session
                ORDER BY p.TurnSequence, p.ItemSequence;
                """;
            read.Parameters.AddWithValue("$session", sessionId.ToString());
            using var reader = read.ExecuteReader();
            while (reader.Read()) entries.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }
        foreach (var entry in entries)
        {
            var turn = new AgentTurnRecord(entry.Turn, sessionId, AgentMessageRole.Assistant, AgentTurnKind.ToolCall, [], default, default)
                { TurnSequence = entry.Sequence, RunId = entry.Run, RunRevision = entry.Revision };
            AssignInvocationGroup(connection, transaction, turn, entry.Entry, entry.Item, entry.Tool, entry.State);
        }
        if (invalidateContext) InvalidateSessionContext(connection, transaction, sessionId);
        using var invalidate = connection.CreateCommand();
        invalidate.Transaction = transaction;
        // Presentation membership has its own epoch. A normal result revision may
        // invalidate provider context without invalidating every expanded group.
        invalidate.CommandText = "UPDATE AgentSessions SET PresentationEpoch = PresentationEpoch + 1 WHERE SessionId = $session;";
        invalidate.Parameters.AddWithValue("$session", sessionId.ToString());
        invalidate.ExecuteNonQuery();
    }
}

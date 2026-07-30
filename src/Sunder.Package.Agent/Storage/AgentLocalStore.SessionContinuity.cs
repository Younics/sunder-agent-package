using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const int MaxHistoricalGeneratedContextCheckpoints = 12;
    private const int MaxSessionContextSummaryLength = 12_000;
    private const int MaxSessionContextDetailsLength = 64_000;
    private const string SessionContextCheckpointColumns =
        "ContextCheckpointId, SessionId, FirstOmittedTurnId, LastOmittedTurnId, "
        + "OmittedTurnCount, SummaryText, DetailsJson, CreatedAtUtc, CheckpointKind, "
        + "TranscriptEpoch, CoveredThroughCreatedAtUtc, CoveredThroughContentRevision, "
        + "SourceRunId, SourceRunRevision, SourceRunEpoch, Generation, "
        + "PreviousContextCheckpointId, GeneratorVersion, ProviderId, ModelId";

    internal long GetTranscriptEpoch(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TranscriptEpoch FROM AgentSessions WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        return command.ExecuteScalar() is long epoch
            ? epoch
            : throw new InvalidOperationException($"Session '{sessionId}' was not found.");
    }

    internal AgentSessionContinuitySnapshot? ReadSessionContinuitySnapshot(
        Guid sessionId,
        Guid sourceRunId,
        long sourceRunRevision)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        if (!TryReadSessionContinuityState(
                connection,
                transaction,
                sessionId,
                out var transcriptEpoch,
                out var activeCheckpointId,
                out var activeGeneration))
        {
            transaction.Commit();
            return null;
        }

        var sourceRun = GetRun(connection, transaction, sourceRunId);
        if (sourceRun is null
            || sourceRun.Key.SessionId != sessionId
            || sourceRun.Key.RunRevision != sourceRunRevision
            || sourceRun.Status != AgentDurableRunStatus.Running
            || sourceRun.FinishedAtUtc is not null
            || HasNewerRun(connection, transaction, sessionId, sourceRunRevision))
        {
            transaction.Commit();
            return null;
        }

        var turns = ListTurns(connection, sessionId, transaction);
        var activeCheckpoint = ReadValidActiveSessionContextCheckpoint(
            connection,
            transaction,
            sessionId,
            transcriptEpoch,
            activeCheckpointId,
            activeGeneration,
            turns);
        transaction.Commit();
        return new AgentSessionContinuitySnapshot(
            sessionId,
            transcriptEpoch,
            activeCheckpointId,
            activeGeneration,
            sourceRun.Key,
            sourceRun.Epoch,
            sourceRun.UserTurnId
            ?? (sourceRun.Suspension switch
            {
                AgentPermissionRunSuspension permission => permission.UserTurnId,
                AgentChildJoinRunSuspension childJoin => childJoin.UserTurnId,
                _ => Guid.Empty,
            }),
            turns,
            activeCheckpoint);
    }

    internal AgentAnchoredSessionContextCheckpoint? GetActiveAnchoredSessionContextCheckpoint(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!TryReadSessionContinuityState(
                connection,
                transaction,
                sessionId,
                out var transcriptEpoch,
                out var activeCheckpointId,
                out var activeGeneration))
        {
            transaction.Commit();
            return null;
        }

        var turns = ListTurns(connection, sessionId, transaction);
        var checkpoint = ReadValidActiveSessionContextCheckpoint(
            connection,
            transaction,
            sessionId,
            transcriptEpoch,
            activeCheckpointId,
            activeGeneration,
            turns);
        transaction.Commit();
        return checkpoint;
    }

    internal AgentAnchoredSessionContextCheckpoint? TrySaveAnchoredSessionContextCheckpoint(
        AgentSessionContextCheckpointSaveRequest request)
    {
        ValidateSessionContextCheckpointSaveRequest(request);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        if (!TryReadSessionContinuityState(
                connection,
                transaction,
                request.SessionId,
                out var transcriptEpoch,
                out var activeCheckpointId,
                out var activeGeneration)
            || transcriptEpoch != request.TranscriptEpoch
            || activeCheckpointId != request.ExpectedActiveContextCheckpointId
            || activeGeneration != request.ExpectedActiveContextGeneration
            || !HasCurrentContinuitySourceRun(connection, transaction, request)
            || !HasExactStableContextPrefix(connection, transaction, request))
        {
            transaction.Rollback();
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var generation = checked(activeGeneration + 1);
        var checkpoint = new AgentAnchoredSessionContextCheckpoint(
            new AgentSessionContextCheckpointRecord(
                Guid.NewGuid(),
                request.SessionId,
                request.FirstOmittedTurnId,
                request.LastOmittedTurnId,
                request.OmittedTurnCount,
                request.SummaryText.Trim(),
                request.DetailsJson,
                now),
            request.Kind,
            request.TranscriptEpoch,
            request.CoveredThroughCreatedAtUtc,
            request.CoveredThroughContentRevision,
            request.SourceRun,
            request.SourceRunEpoch,
            generation,
            request.ExpectedActiveContextCheckpointId,
            request.GeneratorVersion,
            request.ProviderId,
            request.ModelId);

        InsertAnchoredSessionContextCheckpoint(connection, transaction, checkpoint);
        if (!TryActivateSessionContextCheckpoint(
                connection,
                transaction,
                checkpoint,
                request.ExpectedActiveContextCheckpointId,
                request.ExpectedActiveContextGeneration))
        {
            transaction.Rollback();
            return null;
        }

        DeleteExcessGeneratedSessionContextCheckpoints(
            connection,
            transaction,
            request.SessionId,
            checkpoint.Record.ContextCheckpointId);
        transaction.Commit();
        return checkpoint;
    }

    private static bool TryReadSessionContinuityState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        out long transcriptEpoch,
        out Guid? activeCheckpointId,
        out long activeGeneration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TranscriptEpoch, ActiveContextCheckpointId, ActiveContextGeneration FROM AgentSessions WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            transcriptEpoch = 0;
            activeCheckpointId = null;
            activeGeneration = 0;
            return false;
        }

        transcriptEpoch = reader.GetInt64(0);
        activeCheckpointId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
        activeGeneration = reader.GetInt64(2);
        return true;
    }

    private static AgentAnchoredSessionContextCheckpoint? ReadValidActiveSessionContextCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        long transcriptEpoch,
        Guid? activeCheckpointId,
        long activeGeneration,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        if (activeCheckpointId is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SessionContextCheckpointColumns} FROM AgentSessionContextCheckpoints WHERE ContextCheckpointId = $checkpointId AND SessionId = $sessionId;";
        command.Parameters.AddWithValue("$checkpointId", activeCheckpointId.Value.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var checkpoint = ReadAnchoredSessionContextCheckpoint(reader);
        return IsCheckpointAnchoredToTurns(
            checkpoint,
            transcriptEpoch,
            activeGeneration,
            turns)
            ? checkpoint
            : null;
    }

    private static bool IsCheckpointAnchoredToTurns(
        AgentAnchoredSessionContextCheckpoint checkpoint,
        long transcriptEpoch,
        long activeGeneration,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        var record = checkpoint.Record;
        if (checkpoint.Kind == AgentSessionContextCheckpointKind.Legacy
            || checkpoint.TranscriptEpoch != transcriptEpoch
            || checkpoint.Generation != activeGeneration
            || record.OmittedTurnCount <= 0
            || record.FirstOmittedTurnId is null
            || record.LastOmittedTurnId is null
            || checkpoint.CoveredThroughCreatedAtUtc is null
            || checkpoint.CoveredThroughContentRevision is null
            || checkpoint.SourceRun is null
            || checkpoint.SourceRun.Value.SessionId != record.SessionId
            || checkpoint.SourceRunEpoch is null
            || checkpoint.Generation <= 0
            || string.IsNullOrWhiteSpace(checkpoint.GeneratorVersion)
            || turns.Count < record.OmittedTurnCount)
        {
            return false;
        }

        var first = turns[0];
        var anchor = turns[record.OmittedTurnCount - 1];
        return first.TurnId == record.FirstOmittedTurnId
               && anchor.TurnId == record.LastOmittedTurnId
               && anchor.CreatedAtUtc == checkpoint.CoveredThroughCreatedAtUtc
               && anchor.ContentRevision == checkpoint.CoveredThroughContentRevision
               && turns.Take(record.OmittedTurnCount).All(turn => !turn.IsStreaming);
    }

    private static bool HasCurrentContinuitySourceRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSessionContextCheckpointSaveRequest request)
    {
        var sourceRun = GetRun(connection, transaction, request.SourceRun.RunId);
        return sourceRun is not null
               && sourceRun.Key == request.SourceRun
               && sourceRun.Key.SessionId == request.SessionId
               && sourceRun.Epoch == request.SourceRunEpoch
               && sourceRun.Status == AgentDurableRunStatus.Running
               && sourceRun.FinishedAtUtc is null
               && !HasNewerRun(
                   connection,
                   transaction,
                   request.SessionId,
                   request.SourceRun.RunRevision);
    }

    private static bool HasExactStableContextPrefix(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSessionContextCheckpointSaveRequest request)
    {
        AgentTurnRecord? anchor;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns WHERE TurnId = $turnId AND SessionId = $sessionId;";
            command.Parameters.AddWithValue("$turnId", request.LastOmittedTurnId.ToString());
            command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
            using var reader = command.ExecuteReader();
            anchor = reader.Read() ? ReadTurnHeader(reader) : null;
        }

        if (anchor is null
            || anchor.CreatedAtUtc != request.CoveredThroughCreatedAtUtc
            || anchor.ContentRevision != request.CoveredThroughContentRevision
            || anchor.IsStreaming)
        {
            return false;
        }

        using var boundaryCommand = connection.CreateCommand();
        boundaryCommand.Transaction = transaction;
        boundaryCommand.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN IsStreaming = 1 THEN 1 ELSE 0 END),
                (
                    SELECT TurnId
                    FROM AgentTurns firstTurn
                    WHERE firstTurn.SessionId = $sessionId
                    ORDER BY firstTurn.CreatedAtUtc COLLATE BINARY, firstTurn.TurnId COLLATE BINARY
                    LIMIT 1
                )
            FROM AgentTurns
            WHERE SessionId = $sessionId
              AND (
                  CreatedAtUtc COLLATE BINARY < $anchorCreatedAtUtc COLLATE BINARY
                  OR (CreatedAtUtc = $anchorCreatedAtUtc AND TurnId COLLATE BINARY <= $anchorTurnId COLLATE BINARY));
            """;
        boundaryCommand.Parameters.AddWithValue("$sessionId", request.SessionId.ToString());
        boundaryCommand.Parameters.AddWithValue("$anchorCreatedAtUtc", request.CoveredThroughCreatedAtUtc.ToString("O"));
        boundaryCommand.Parameters.AddWithValue("$anchorTurnId", request.LastOmittedTurnId.ToString());
        using var boundaryReader = boundaryCommand.ExecuteReader();
        return boundaryReader.Read()
               && boundaryReader.GetInt64(0) == request.OmittedTurnCount
               && (boundaryReader.IsDBNull(1) || boundaryReader.GetInt64(1) == 0)
               && !boundaryReader.IsDBNull(2)
               && Guid.Parse(boundaryReader.GetString(2)) == request.FirstOmittedTurnId;
    }

    private static void InsertAnchoredSessionContextCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentAnchoredSessionContextCheckpoint checkpoint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentSessionContextCheckpoints (
                ContextCheckpointId, SessionId, FirstOmittedTurnId, LastOmittedTurnId,
                OmittedTurnCount, SummaryText, DetailsJson, CreatedAtUtc, CheckpointKind,
                TranscriptEpoch, CoveredThroughCreatedAtUtc, CoveredThroughContentRevision,
                SourceRunId, SourceRunRevision, SourceRunEpoch, Generation,
                PreviousContextCheckpointId, GeneratorVersion, ProviderId, ModelId)
            VALUES (
                $checkpointId, $sessionId, $firstOmittedTurnId, $lastOmittedTurnId,
                $omittedTurnCount, $summaryText, $detailsJson, $createdAtUtc, $checkpointKind,
                $transcriptEpoch, $coveredThroughCreatedAtUtc, $coveredThroughContentRevision,
                $sourceRunId, $sourceRunRevision, $sourceRunEpoch, $generation,
                $previousContextCheckpointId, $generatorVersion, $providerId, $modelId);
            """;
        command.Parameters.AddWithValue("$checkpointId", checkpoint.Record.ContextCheckpointId.ToString());
        command.Parameters.AddWithValue("$sessionId", checkpoint.Record.SessionId.ToString());
        command.Parameters.AddWithValue("$firstOmittedTurnId", checkpoint.Record.FirstOmittedTurnId!.Value.ToString());
        command.Parameters.AddWithValue("$lastOmittedTurnId", checkpoint.Record.LastOmittedTurnId!.Value.ToString());
        command.Parameters.AddWithValue("$omittedTurnCount", checkpoint.Record.OmittedTurnCount);
        command.Parameters.AddWithValue("$summaryText", checkpoint.Record.SummaryText);
        command.Parameters.AddWithValue("$detailsJson", checkpoint.Record.DetailsJson!);
        command.Parameters.AddWithValue("$createdAtUtc", checkpoint.Record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$checkpointKind", checkpoint.Kind.ToString());
        command.Parameters.AddWithValue("$transcriptEpoch", checkpoint.TranscriptEpoch);
        command.Parameters.AddWithValue("$coveredThroughCreatedAtUtc", checkpoint.CoveredThroughCreatedAtUtc!.Value.ToString("O"));
        command.Parameters.AddWithValue("$coveredThroughContentRevision", checkpoint.CoveredThroughContentRevision!.Value);
        command.Parameters.AddWithValue("$sourceRunId", checkpoint.SourceRun!.Value.RunId.ToString());
        command.Parameters.AddWithValue("$sourceRunRevision", checkpoint.SourceRun.Value.RunRevision);
        command.Parameters.AddWithValue("$sourceRunEpoch", checkpoint.SourceRunEpoch!.Value);
        command.Parameters.AddWithValue("$generation", checkpoint.Generation);
        command.Parameters.AddWithValue("$previousContextCheckpointId", checkpoint.PreviousContextCheckpointId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$generatorVersion", checkpoint.GeneratorVersion!);
        command.Parameters.AddWithValue("$providerId", (object?)checkpoint.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)checkpoint.ModelId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static bool TryActivateSessionContextCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentAnchoredSessionContextCheckpoint checkpoint,
        Guid? expectedCheckpointId,
        long expectedGeneration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentSessions
            SET ActiveContextCheckpointId = $checkpointId,
                ActiveContextGeneration = $generation,
                UpdatedAtUtc = $updatedAtUtc
            WHERE SessionId = $sessionId
              AND TranscriptEpoch = $transcriptEpoch
              AND ActiveContextGeneration = $expectedGeneration
              AND (
                  (ActiveContextCheckpointId IS NULL AND $expectedCheckpointId IS NULL)
                  OR ActiveContextCheckpointId = $expectedCheckpointId);
            """;
        command.Parameters.AddWithValue("$checkpointId", checkpoint.Record.ContextCheckpointId.ToString());
        command.Parameters.AddWithValue("$generation", checkpoint.Generation);
        command.Parameters.AddWithValue("$updatedAtUtc", checkpoint.Record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", checkpoint.Record.SessionId.ToString());
        command.Parameters.AddWithValue("$transcriptEpoch", checkpoint.TranscriptEpoch);
        command.Parameters.AddWithValue("$expectedGeneration", expectedGeneration);
        command.Parameters.AddWithValue("$expectedCheckpointId", expectedCheckpointId?.ToString() ?? (object)DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    private static void DeleteExcessGeneratedSessionContextCheckpoints(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        Guid activeCheckpointId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM AgentSessionContextCheckpoints
            WHERE SessionId = $sessionId
              AND CheckpointKind <> 'Legacy'
              AND ContextCheckpointId <> $activeCheckpointId
              AND ContextCheckpointId NOT IN (
                  SELECT ContextCheckpointId
                  FROM AgentSessionContextCheckpoints
                  WHERE SessionId = $sessionId
                    AND CheckpointKind <> 'Legacy'
                  ORDER BY Generation DESC, CreatedAtUtc DESC, ContextCheckpointId DESC
                  LIMIT $historicalLimit);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$activeCheckpointId", activeCheckpointId.ToString());
        command.Parameters.AddWithValue("$historicalLimit", MaxHistoricalGeneratedContextCheckpoints);
        command.ExecuteNonQuery();
    }

    private static void InvalidateSessionContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentSessions
            SET TranscriptEpoch = TranscriptEpoch + 1,
                ActiveContextCheckpointId = NULL,
                ActiveContextGeneration = ActiveContextGeneration + 1
            WHERE SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Session '{sessionId}' was not found while invalidating transcript context.");
        }
    }

    private static AgentAnchoredSessionContextCheckpoint ReadAnchoredSessionContextCheckpoint(SqliteDataReader reader)
    {
        AgentDurableRunKey? sourceRun = reader.IsDBNull(12) || reader.IsDBNull(13)
            ? null
            : new AgentDurableRunKey(
                Guid.Parse(reader.GetString(12)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt64(13));
        return new AgentAnchoredSessionContextCheckpoint(
            ReadSessionContextCheckpoint(reader),
            Enum.Parse<AgentSessionContextCheckpointKind>(reader.GetString(8), ignoreCase: false),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            sourceRun,
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            reader.GetInt64(15),
            reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19));
    }

    private static void ValidateSessionContextCheckpointSaveRequest(AgentSessionContextCheckpointSaveRequest request)
    {
        if (request.SessionId == Guid.Empty
            || request.SourceRun.SessionId != request.SessionId
            || request.SourceRun.RunId == Guid.Empty
            || request.SourceRun.RunRevision <= 0
            || request.SourceRunEpoch <= 0
            || request.TranscriptEpoch <= 0
            || request.ExpectedActiveContextGeneration < 0
            || request.FirstOmittedTurnId == Guid.Empty
            || request.LastOmittedTurnId == Guid.Empty
            || request.OmittedTurnCount <= 0
            || request.CoveredThroughContentRevision <= 0
            || request.Kind == AgentSessionContextCheckpointKind.Legacy
            || string.IsNullOrWhiteSpace(request.SummaryText)
            || request.SummaryText.Length > MaxSessionContextSummaryLength
            || string.IsNullOrWhiteSpace(request.DetailsJson)
            || request.DetailsJson.Length > MaxSessionContextDetailsLength
            || string.IsNullOrWhiteSpace(request.GeneratorVersion))
        {
            throw new InvalidOperationException("The anchored session context checkpoint request is invalid.");
        }
    }

    private static string? ReadLatestWorkingSummary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        if (!TryReadSessionContinuityState(
                connection,
                transaction,
                sessionId,
                out var transcriptEpoch,
                out var activeCheckpointId,
                out var activeGeneration))
        {
            return null;
        }

        return ReadValidActiveSessionContextCheckpoint(
            connection,
            transaction,
            sessionId,
            transcriptEpoch,
            activeCheckpointId,
            activeGeneration,
            ListTurns(connection, sessionId, transaction))?.Record.SummaryText;
    }
}

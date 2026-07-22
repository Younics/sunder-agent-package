using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static IReadOnlyList<AgentCompletedStreamingTurn> CompleteStreamingTextTurns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey runKey,
        DateTimeOffset updatedAtUtc)
    {
        var turns = new List<(Guid TurnId, int ContentLength)>();
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT t.TurnId, COALESCE(i.TextContent, '')
                FROM AgentTurns t
                LEFT JOIN AgentTurnItems i
                  ON i.TurnId = t.TurnId
                 AND i.SequenceNumber = 0
                 AND i.Kind = 'Text'
                WHERE t.SessionId = $sessionId
                  AND t.Role = 'Assistant'
                  AND t.Kind = 'Message'
                  AND t.IsStreaming = 1
                  AND t.RunId = $runId
                  AND t.RunRevision = $runRevision
                ORDER BY t.CreatedAtUtc, t.TurnId;
                """;
            readCommand.Parameters.AddWithValue("$sessionId", runKey.SessionId.ToString());
            readCommand.Parameters.AddWithValue("$runId", runKey.RunId.ToString());
            readCommand.Parameters.AddWithValue("$runRevision", runKey.RunRevision);
            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                turns.Add((
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1).Length));
            }
        }

        if (turns.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentTurns
            SET UpdatedAtUtc = $updatedAtUtc,
                ContentRevision = ContentRevision + 1,
                IsStreaming = 0
            WHERE SessionId = $sessionId
              AND Role = 'Assistant'
              AND Kind = 'Message'
              AND IsStreaming = 1
              AND RunId = $runId
              AND RunRevision = $runRevision;
            """;
        command.Parameters.AddWithValue("$updatedAtUtc", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", runKey.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", runKey.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", runKey.RunRevision);
        command.ExecuteNonQuery();
        return turns
            .Select(turn => new AgentCompletedStreamingTurn(
                GetTurn(connection, turn.TurnId, transaction)
                ?? throw new InvalidOperationException(
                    $"Completed streaming turn '{turn.TurnId}' could not be reloaded."),
                turn.ContentLength))
            .ToArray();
    }

    public AgentTurnRecord UpdateTextTurn(Guid messageId, string content)
    {
        using var connection = CreateConnection();
        connection.Open();

        var existingTurn = GetTurn(connection, messageId) ?? throw new InvalidOperationException($"Message '{messageId}' was not found.");
        if (!CanUpdateProjectedMessage(existingTurn))
        {
            throw new InvalidOperationException($"Turn '{messageId}' does not support in-place text updates.");
        }

        var updatedAtUtc = DateTimeOffset.UtcNow;
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE AgentTurns SET UpdatedAtUtc = $updatedAtUtc, ContentRevision = ContentRevision + 1, IsStreaming = 0 WHERE TurnId = $id;";
        command.Parameters.AddWithValue("$updatedAtUtc", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", messageId.ToString());
        command.ExecuteNonQuery();

        using var updateItem = connection.CreateCommand();
        updateItem.Transaction = transaction;
        updateItem.CommandText = "UPDATE AgentTurnItems SET TextContent = $content WHERE TurnId = $turnId AND SequenceNumber = 0;";
        updateItem.Parameters.AddWithValue("$content", content);
        updateItem.Parameters.AddWithValue("$turnId", messageId.ToString());
        updateItem.ExecuteNonQuery();

        TouchSession(connection, existingTurn.SessionId, null, null, transaction);
        var turn = GetTurn(connection, messageId, transaction)
                   ?? throw new InvalidOperationException($"Turn '{messageId}' was not found after update.");
        transaction.Commit();
        return turn;
    }

    internal AgentTurnWriteResult? TryUpdateTextTurn(
        AgentDurableRunKey runKey,
        long expectedEpoch,
        Guid turnId,
        string content)
    {
        BeforeFencedTranscriptTransaction?.Invoke(AgentTranscriptMutationKind.AssistantText);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!CanMutateTranscript(connection, transaction, runKey, expectedEpoch))
        {
            transaction.Rollback();
            return null;
        }

        long previousRevision;
        string previousContent;
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT t.ContentRevision, i.TextContent
                FROM AgentTurns t
                INNER JOIN AgentTurnItems i ON i.TurnId = t.TurnId AND i.SequenceNumber = 0 AND i.Kind = 'Text'
                WHERE t.TurnId = $turnId
                  AND t.SessionId = $sessionId
                  AND t.RunId = $runId
                  AND t.RunRevision = $runRevision
                  AND t.Role = 'Assistant'
                  AND t.Kind = 'Message';
                """;
            readCommand.Parameters.AddWithValue("$turnId", turnId.ToString());
            readCommand.Parameters.AddWithValue("$sessionId", runKey.SessionId.ToString());
            readCommand.Parameters.AddWithValue("$runId", runKey.RunId.ToString());
            readCommand.Parameters.AddWithValue("$runRevision", runKey.RunRevision);
            using var reader = readCommand.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return null;
            }

            previousRevision = reader.GetInt64(0);
            previousContent = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        }

        var mutationKind = content.StartsWith(previousContent, StringComparison.Ordinal)
            ? AgentTurnMutationKind.Append
            : AgentTurnMutationKind.Replace;
        var mutationText = mutationKind == AgentTurnMutationKind.Append
            ? content[previousContent.Length..]
            : content;
        var updatedAtUtc = DateTimeOffset.UtcNow;
        var contentRevision = previousRevision + 1;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE AgentTurns SET UpdatedAtUtc = $updatedAtUtc, ContentRevision = $contentRevision, IsStreaming = 1 WHERE TurnId = $turnId AND SessionId = $sessionId AND RunId = $runId AND RunRevision = $runRevision AND Role = 'Assistant' AND Kind = 'Message';";
            command.Parameters.AddWithValue("$updatedAtUtc", updatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$contentRevision", contentRevision);
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            command.Parameters.AddWithValue("$sessionId", runKey.SessionId.ToString());
            command.Parameters.AddWithValue("$runId", runKey.RunId.ToString());
            command.Parameters.AddWithValue("$runRevision", runKey.RunRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = mutationKind == AgentTurnMutationKind.Append
                ? "UPDATE AgentTurnItems SET TextContent = COALESCE(TextContent, '') || $content WHERE TurnId = $turnId AND SequenceNumber = 0 AND Kind = 'Text';"
                : "UPDATE AgentTurnItems SET TextContent = $content WHERE TurnId = $turnId AND SequenceNumber = 0 AND Kind = 'Text';";
            command.Parameters.AddWithValue("$content", mutationText);
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        TouchSession(connection, runKey.SessionId, null, null, transaction);
        var turn = GetTurn(connection, turnId, transaction);
        transaction.Commit();
        return turn is null
            ? null
            : new AgentTurnWriteResult(
                turn,
                mutationKind,
                previousContent.Length,
                mutationText);
    }

    internal AgentTurnWriteResult? TryCompleteTextTurn(
        AgentDurableRunKey runKey,
        long expectedEpoch,
        Guid turnId)
    {
        BeforeFencedTranscriptTransaction?.Invoke(AgentTranscriptMutationKind.AssistantText);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!CanMutateTranscript(connection, transaction, runKey, expectedEpoch))
        {
            transaction.Rollback();
            return null;
        }

        long previousRevision;
        string content;
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT t.ContentRevision, i.TextContent
                FROM AgentTurns t
                INNER JOIN AgentTurnItems i ON i.TurnId = t.TurnId AND i.SequenceNumber = 0 AND i.Kind = 'Text'
                WHERE t.TurnId = $turnId
                  AND t.SessionId = $sessionId
                  AND t.RunId = $runId
                  AND t.RunRevision = $runRevision
                  AND t.Role = 'Assistant'
                  AND t.Kind = 'Message';
                """;
            readCommand.Parameters.AddWithValue("$turnId", turnId.ToString());
            readCommand.Parameters.AddWithValue("$sessionId", runKey.SessionId.ToString());
            readCommand.Parameters.AddWithValue("$runId", runKey.RunId.ToString());
            readCommand.Parameters.AddWithValue("$runRevision", runKey.RunRevision);
            using var reader = readCommand.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return null;
            }

            previousRevision = reader.GetInt64(0);
            content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        }

        var updatedAtUtc = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentTurns
                SET UpdatedAtUtc = $updatedAtUtc,
                    ContentRevision = ContentRevision + 1,
                    IsStreaming = 0
                WHERE TurnId = $turnId
                  AND SessionId = $sessionId
                  AND RunId = $runId
                  AND RunRevision = $runRevision
                  AND Role = 'Assistant'
                  AND Kind = 'Message';
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", updatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            command.Parameters.AddWithValue("$sessionId", runKey.SessionId.ToString());
            command.Parameters.AddWithValue("$runId", runKey.RunId.ToString());
            command.Parameters.AddWithValue("$runRevision", runKey.RunRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        var turn = GetTurn(connection, turnId, transaction);
        transaction.Commit();
        return turn is null
            ? null
            : new AgentTurnWriteResult(
                turn,
                AgentTurnMutationKind.Complete,
                content.Length,
                Text: null);
    }
}

using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentRunStartPersistenceResult? TryStartRun(
        AgentDurableRunKey runKey,
        long expectedEpoch,
        string userMessage,
        IReadOnlyList<AgentStoredAttachment> attachments,
        Guid? rollbackAnchorTurnId,
        string runningSummary)
    {
        BeforeFencedTranscriptTransaction?.Invoke(AgentTranscriptMutationKind.UserRunStart);

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!CanStartRun(connection, transaction, runKey, expectedEpoch))
        {
            transaction.Rollback();
            return null;
        }

        AgentTranscriptRollbackResult? rollback = null;
        if (rollbackAnchorTurnId is { } anchorTurnId)
        {
            rollback = RollbackTranscript(
                connection,
                transaction,
                runKey.SessionId,
                anchorTurnId);
        }

        var now = DateTimeOffset.UtcNow;
        var userTurn = attachments.Count == 0
            ? CreateTextTurn(
                Guid.NewGuid(),
                runKey.SessionId,
                AgentMessageRole.User,
                AgentTurnKind.Message,
                userMessage,
                now,
                now)
            : CreateMessageTurn(
                Guid.NewGuid(),
                runKey.SessionId,
                AgentMessageRole.User,
                userMessage,
                attachments,
                now,
                now);
        InsertTurn(connection, transaction, userTurn, runKey: runKey);

        var transition = TryTransitionRun(
            connection,
            transaction,
            runKey,
            expectedEpoch,
            AgentRunStatus.Running,
            runningSummary);
        if (transition is null)
        {
            transaction.Rollback();
            return null;
        }

        transaction.Commit();
        return new AgentRunStartPersistenceResult(transition, userTurn, rollback);
    }

    private static bool CanStartRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentDurableRunKey key,
        long expectedEpoch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM AgentRuns
            WHERE RunId = $runId
              AND SessionId = $sessionId
              AND RunRevision = $runRevision
              AND Epoch = $expectedEpoch
              AND Status IN ('Preparing', 'Idle')
              AND FinishedAtUtc IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM AgentRuns newer
                  WHERE newer.SessionId = $sessionId
                    AND newer.RunRevision > $runRevision)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
        command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
        return command.ExecuteScalar() is not null;
    }
}

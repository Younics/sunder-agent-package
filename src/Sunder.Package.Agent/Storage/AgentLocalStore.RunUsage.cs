using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentRunBudgetState? ChargeRunBudget(
        AgentDurableRunKey key,
        long expectedEpoch,
        AgentRunBudgetCharge charge)
    {
        if (charge.ProviderCycles < 0
            || charge.ToolCalls < 0
            || charge.SubmittedContextTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(charge));
        }

        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentRuns
            SET ProviderCycleCount = CASE WHEN ProviderCycleCount > 9223372036854775807 - $providerCycles
                    THEN 9223372036854775807 ELSE ProviderCycleCount + $providerCycles END,
                ToolCallCount = CASE WHEN ToolCallCount > 9223372036854775807 - $toolCalls
                    THEN 9223372036854775807 ELSE ToolCallCount + $toolCalls END,
                SubmittedContextTokenCount = CASE WHEN SubmittedContextTokenCount > 9223372036854775807 - $submittedContextTokens
                    THEN 9223372036854775807 ELSE SubmittedContextTokenCount + $submittedContextTokens END,
                UpdatedAtUtc = $updatedAtUtc
            WHERE RunId = $runId
              AND SessionId = $sessionId
              AND RunRevision = $runRevision
              AND Epoch = $expectedEpoch
              AND Status = 'Running'
              AND FinishedAtUtc IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM AgentRuns newer
                  WHERE newer.SessionId = $sessionId
                    AND newer.RunRevision > $runRevision);
            """;
        command.Parameters.AddWithValue("$providerCycles", charge.ProviderCycles);
        command.Parameters.AddWithValue("$toolCalls", charge.ToolCalls);
        command.Parameters.AddWithValue("$submittedContextTokens", charge.SubmittedContextTokens);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$runId", key.RunId.ToString());
        command.Parameters.AddWithValue("$sessionId", key.SessionId.ToString());
        command.Parameters.AddWithValue("$runRevision", key.RunRevision);
        command.Parameters.AddWithValue("$expectedEpoch", expectedEpoch);
        if (command.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return null;
        }

        var run = GetRun(connection, transaction, key.RunId)
            ?? throw new InvalidOperationException("The charged durable run could not be reloaded.");
        transaction.Commit();
        return run.BudgetState;
    }
}

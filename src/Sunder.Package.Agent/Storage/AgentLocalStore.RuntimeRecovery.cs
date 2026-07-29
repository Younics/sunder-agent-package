namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal int RecoverLifecycleDeliveryLeases(
        DateTimeOffset now,
        int limit = MaxLifecycleReconciliationBatchSize)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentLifecycleDeliveries
            SET Status = 'Pending',
                NextAttemptAtUtc = $now,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL
            WHERE rowid IN (
                SELECT rowid
                FROM AgentLifecycleDeliveries
                WHERE Status = 'InFlight'
                ORDER BY EventSequence
                LIMIT $limit);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxLifecycleReconciliationBatchSize));
        var recovered = command.ExecuteNonQuery();
        transaction.Commit();
        return recovered;
    }

    internal int RecoverPoisonedLifecycleDeliveries(
        DateTimeOffset now,
        int limit = MaxLifecycleReconciliationBatchSize)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentLifecycleDeliveries
            SET Status = 'Pending',
                NextAttemptAtUtc = $now,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL
            WHERE rowid IN (
                SELECT rowid
                FROM AgentLifecycleDeliveries
                WHERE Status = 'Poison'
                ORDER BY EventSequence
                LIMIT $limit);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxLifecycleReconciliationBatchSize));
        var recovered = command.ExecuteNonQuery();
        transaction.Commit();
        if (recovered > 0)
        {
            SignalLifecycleOutboxChanged();
        }
        return recovered;
    }

    internal void RecoverRuntimeState()
    {
        RecoverToolExecutions();
        RecoverInterruptedPermissionClaims();
        RecoverAmbiguousParentContinuationWork();
        RecoverUnownedActiveRuns();
        var now = DateTimeOffset.UtcNow;
        while (RecoverLifecycleDeliveryLeases(now) == MaxLifecycleReconciliationBatchSize)
        {
        }
        while (RecoverPoisonedLifecycleDeliveries(now) == MaxLifecycleReconciliationBatchSize)
        {
        }
        while (RecoverSessionCleanupJobLeases(now) == MaxSessionCleanupJobsPerPass)
        {
        }
    }
}

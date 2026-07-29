namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal const int MaxLifecycleMaintenanceBatchSize = 256;
    internal static readonly TimeSpan LifecycleSubscriptionRetirementAge = TimeSpan.FromDays(30);
    internal static readonly TimeSpan LifecycleCompactionRetention = TimeSpan.FromDays(7);

    internal bool MaintainLifecycleState(DateTimeOffset now)
    {
        var retired = RetireInactiveLifecycleSubscriptions(
            now - LifecycleSubscriptionRetirementAge,
            now);
        var staleDeliveries = DeleteStaleLifecycleDeliveries();
        var compactedEvents = CompactLifecycleOutbox(now - LifecycleCompactionRetention);
        var cleanupReceipts = DeleteCompletedSessionCleanupJobs(now - LifecycleCompactionRetention);
        return retired == MaxLifecycleMaintenanceBatchSize
               || staleDeliveries == MaxLifecycleMaintenanceBatchSize
               || compactedEvents == MaxLifecycleMaintenanceBatchSize
               || cleanupReceipts == MaxLifecycleMaintenanceBatchSize;
    }

    internal int RetireInactiveLifecycleSubscriptions(
        DateTimeOffset inactiveBefore,
        DateTimeOffset now,
        int limit = MaxLifecycleMaintenanceBatchSize)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentLifecycleSubscriptions
            SET RetiredAtUtc = $now
            WHERE SubscriptionId IN (
                SELECT SubscriptionId
                FROM AgentLifecycleSubscriptions
                WHERE RetiredAtUtc IS NULL
                  AND LastSeenAtUtc < $inactiveBefore
                ORDER BY LastSeenAtUtc, SubscriptionId
                LIMIT $limit);
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$inactiveBefore", inactiveBefore.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxLifecycleMaintenanceBatchSize));
        return command.ExecuteNonQuery();
    }

    internal int DeleteStaleLifecycleDeliveries(int limit = MaxLifecycleMaintenanceBatchSize)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM AgentLifecycleDeliveries
            WHERE rowid IN (
                SELECT delivery.rowid
                FROM AgentLifecycleDeliveries delivery
                INNER JOIN AgentLifecycleSubscriptions subscription
                    ON subscription.SubscriptionId = delivery.SubscriptionId
                WHERE subscription.RetiredAtUtc IS NOT NULL
                   OR delivery.MembershipGeneration <> subscription.MembershipGeneration
                ORDER BY delivery.EventSequence, delivery.SubscriptionId
                LIMIT $limit);
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxLifecycleMaintenanceBatchSize));
        return command.ExecuteNonQuery();
    }

    internal int CompactLifecycleOutbox(
        DateTimeOffset createdBefore,
        int limit = MaxLifecycleMaintenanceBatchSize)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var sequences = new List<long>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT candidate.Sequence
                FROM AgentLifecycleOutbox candidate
                WHERE candidate.CreatedAtUtc < $createdBefore
                  AND (candidate.PayloadState = 'Erased'
                       OR candidate.EventType IN ('SessionDeleted', 'WorkspaceDeleted'))
                  AND (
                      candidate.EventType IN ('SessionDeleted', 'WorkspaceDeleted')
                      OR
                      (candidate.SessionId IS NOT NULL AND NOT EXISTS (
                          SELECT 1 FROM AgentSessions session
                          WHERE session.SessionId = candidate.SessionId))
                      OR
                      (candidate.WorkspaceId IS NOT NULL AND NOT EXISTS (
                          SELECT 1 FROM AgentWorkspaces workspace
                          WHERE workspace.WorkspaceId = candidate.WorkspaceId))
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentLifecycleSubscriptions subscription
                      WHERE subscription.RetiredAtUtc IS NULL
                        AND (subscription.ContractKind = 'Durable'
                             OR candidate.EventType IN (
                                 'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                                 'RunInterrupted', 'RunStopped', 'RunFailed'))
                        AND (candidate.PayloadState = 'Available'
                             OR subscription.MembershipGeneration <= candidate.PayloadErasureGeneration)
                        AND NOT EXISTS (
                            SELECT 1
                            FROM AgentLifecycleDeliveries delivery
                            WHERE delivery.SubscriptionId = subscription.SubscriptionId
                              AND delivery.EventSequence = candidate.Sequence
                              AND delivery.MembershipGeneration = subscription.MembershipGeneration
                              AND delivery.Status = 'Delivered')
                  )
                ORDER BY candidate.Sequence
                LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$createdBefore", createdBefore.ToString("O"));
            select.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxLifecycleMaintenanceBatchSize));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                sequences.Add(reader.GetInt64(0));
            }
        }

        if (sequences.Count == 0)
        {
            transaction.Commit();
            return 0;
        }

        using (var enable = connection.CreateCommand())
        {
            enable.Transaction = transaction;
            enable.CommandText = "UPDATE AgentLifecycleMaintenanceState SET AllowCompaction = 1 WHERE SingletonId = 1;";
            enable.ExecuteNonQuery();
        }

        var parameterNames = new string[sequences.Count];
        using (var deleteEvents = connection.CreateCommand())
        {
            deleteEvents.Transaction = transaction;
            for (var index = 0; index < sequences.Count; index++)
            {
                parameterNames[index] = $"$sequence{index}";
                deleteEvents.Parameters.AddWithValue(parameterNames[index], sequences[index]);
            }
            deleteEvents.CommandText = $"DELETE FROM AgentLifecycleOutbox WHERE Sequence IN ({string.Join(", ", parameterNames)});";
            deleteEvents.ExecuteNonQuery();
        }

        using (var deleteDeliveries = connection.CreateCommand())
        {
            deleteDeliveries.Transaction = transaction;
            deleteDeliveries.CommandText = $"DELETE FROM AgentLifecycleDeliveries WHERE EventSequence IN ({string.Join(", ", parameterNames)});";
            for (var index = 0; index < sequences.Count; index++)
            {
                deleteDeliveries.Parameters.AddWithValue(parameterNames[index], sequences[index]);
            }
            deleteDeliveries.ExecuteNonQuery();
        }

        using (var disable = connection.CreateCommand())
        {
            disable.Transaction = transaction;
            disable.CommandText = "UPDATE AgentLifecycleMaintenanceState SET AllowCompaction = 0 WHERE SingletonId = 1;";
            disable.ExecuteNonQuery();
        }

        transaction.Commit();
        return sequences.Count;
    }

    internal int DeleteCompletedSessionCleanupJobs(
        DateTimeOffset completedBefore,
        int limit = MaxLifecycleMaintenanceBatchSize)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM AgentSessionCleanupJobs
            WHERE JobId IN (
                SELECT JobId
                FROM AgentSessionCleanupJobs
                WHERE Status = 'Completed'
                  AND CompletedAtUtc < $completedBefore
                ORDER BY CompletedAtUtc, JobId
                LIMIT $limit);
            """;
        command.Parameters.AddWithValue("$completedBefore", completedBefore.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxLifecycleMaintenanceBatchSize));
        return command.ExecuteNonQuery();
    }
}

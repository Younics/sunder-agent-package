namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal bool ReconcileLifecycleSubscription(AgentLifecycleSubscription subscription, DateTimeOffset now)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        long highWaterSequence;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(Sequence), 0) FROM AgentLifecycleOutbox;";
            highWaterSequence = Convert.ToInt64(command.ExecuteScalar());
        }

        long reconciledThroughSequence = 0;
        long membershipGeneration = 0;
        var existingSubscription = false;
        var retiredSubscription = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ReconciledThroughSequence, MembershipGeneration, RetiredAtUtc
                FROM AgentLifecycleSubscriptions
                WHERE SubscriptionId = $subscriptionId;
                """;
            command.Parameters.AddWithValue("$subscriptionId", subscription.SubscriptionId);
            using var existingReader = command.ExecuteReader();
            if (existingReader.Read())
            {
                existingSubscription = true;
                reconciledThroughSequence = existingReader.GetInt64(0);
                membershipGeneration = existingReader.GetInt64(1);
                retiredSubscription = !existingReader.IsDBNull(2);
            }
        }

        if (!existingSubscription || retiredSubscription)
        {
            membershipGeneration = AdvanceLifecycleGeneration(connection, transaction);
            long replayStartSequence;
            using (var replayCommand = connection.CreateCommand())
            {
                replayCommand.Transaction = transaction;
                replayCommand.CommandText = $"""
                    SELECT MIN(Sequence)
                    FROM AgentLifecycleOutbox
                    WHERE Sequence <= $highWaterSequence
                      AND PayloadState = 'Available'
                      AND {LifecycleContractEligibilitySql};
                    """;
                replayCommand.Parameters.AddWithValue("$highWaterSequence", highWaterSequence);
                replayCommand.Parameters.AddWithValue("$contractKind", subscription.ContractKind);
                var firstReplaySequence = replayCommand.ExecuteScalar();
                replayStartSequence = firstReplaySequence is null || firstReplaySequence is DBNull
                    ? checked(highWaterSequence + 1)
                    : Convert.ToInt64(firstReplaySequence);
            }
            reconciledThroughSequence = replayStartSequence - 1;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = existingSubscription
                ? """
                    UPDATE AgentLifecycleSubscriptions
                    SET PackageId = $packageId,
                        ObserverId = $observerId,
                        ContractKind = $contractKind,
                        DisplayName = $displayName,
                        LastSeenAtUtc = $now,
                        ReplayStartSequence = $replayStartSequence,
                        ReconciledThroughSequence = $reconciledThroughSequence,
                        MembershipGeneration = $membershipGeneration,
                        RetiredAtUtc = NULL
                    WHERE SubscriptionId = $subscriptionId;
                    """
                : """
                    INSERT INTO AgentLifecycleSubscriptions (
                        SubscriptionId, PackageId, ObserverId, ContractKind, DisplayName,
                        CreatedAtUtc, LastSeenAtUtc, ReplayStartSequence, ReconciledThroughSequence,
                        MembershipGeneration, RetiredAtUtc)
                    VALUES (
                        $subscriptionId, $packageId, $observerId, $contractKind, $displayName,
                        $now, $now, $replayStartSequence, $reconciledThroughSequence,
                        $membershipGeneration, NULL);
                    """;
            BindSubscription(command, subscription);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$replayStartSequence", replayStartSequence);
            command.Parameters.AddWithValue("$reconciledThroughSequence", reconciledThroughSequence);
            command.Parameters.AddWithValue("$membershipGeneration", membershipGeneration);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentLifecycleSubscriptions
                SET DisplayName = $displayName,
                    LastSeenAtUtc = $now
                WHERE SubscriptionId = $subscriptionId;
                """;
            command.Parameters.AddWithValue("$displayName", BoundLifecycleText(subscription.DisplayName, 512) ?? string.Empty);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$subscriptionId", subscription.SubscriptionId);
            command.ExecuteNonQuery();
        }

        var replaySequences = new List<long>(MaxLifecycleReconciliationBatchSize);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT Sequence
                FROM AgentLifecycleOutbox
                WHERE Sequence > $reconciledThroughSequence
                  AND Sequence <= $highWaterSequence
                  AND {LifecycleReplayEligibilitySql}
                ORDER BY Sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$reconciledThroughSequence", reconciledThroughSequence);
            command.Parameters.AddWithValue("$highWaterSequence", highWaterSequence);
            command.Parameters.AddWithValue("$contractKind", subscription.ContractKind);
            command.Parameters.AddWithValue("$membershipGeneration", membershipGeneration);
            command.Parameters.AddWithValue("$limit", MaxLifecycleReconciliationBatchSize);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                replaySequences.Add(reader.GetInt64(0));
            }
        }

        if (replaySequences.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO AgentLifecycleDeliveries (
                    SubscriptionId, EventSequence, Status, AttemptCount, NextAttemptAtUtc,
                    MembershipGeneration, DeliveryVersion)
                SELECT $subscriptionId, Sequence, 'Pending', 0, $now,
                       $membershipGeneration, 1
                FROM AgentLifecycleOutbox
                WHERE Sequence > $reconciledThroughSequence
                  AND Sequence <= $batchEndSequence
                  AND {LifecycleReplayEligibilitySql}
                ORDER BY Sequence
                ON CONFLICT(SubscriptionId, EventSequence) DO UPDATE SET
                    Status = 'Pending',
                    AttemptCount = 0,
                    NextAttemptAtUtc = excluded.NextAttemptAtUtc,
                    LeaseToken = NULL,
                    LeaseExpiresAtUtc = NULL,
                    LastAttemptAtUtc = NULL,
                    DeliveredAtUtc = NULL,
                    PoisonedAtUtc = NULL,
                    LastError = NULL,
                    MembershipGeneration = excluded.MembershipGeneration,
                    DeliveryVersion = AgentLifecycleDeliveries.DeliveryVersion + 1
                WHERE AgentLifecycleDeliveries.MembershipGeneration <> excluded.MembershipGeneration;
                """;
            command.Parameters.AddWithValue("$subscriptionId", subscription.SubscriptionId);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$reconciledThroughSequence", reconciledThroughSequence);
            command.Parameters.AddWithValue("$batchEndSequence", replaySequences[^1]);
            command.Parameters.AddWithValue("$contractKind", subscription.ContractKind);
            command.Parameters.AddWithValue("$membershipGeneration", membershipGeneration);
            command.ExecuteNonQuery();
        }

        var hasMore = false;
        if (replaySequences.Count == MaxLifecycleReconciliationBatchSize)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT 1
                FROM AgentLifecycleOutbox
                WHERE Sequence > $batchEndSequence
                  AND Sequence <= $highWaterSequence
                  AND {LifecycleReplayEligibilitySql}
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$batchEndSequence", replaySequences[^1]);
            command.Parameters.AddWithValue("$highWaterSequence", highWaterSequence);
            command.Parameters.AddWithValue("$contractKind", subscription.ContractKind);
            command.Parameters.AddWithValue("$membershipGeneration", membershipGeneration);
            hasMore = command.ExecuteScalar() is not null;
        }

        var newCursor = hasMore ? replaySequences[^1] : highWaterSequence;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AgentLifecycleSubscriptions
                SET ReconciledThroughSequence = $reconciledThroughSequence
                WHERE SubscriptionId = $subscriptionId
                  AND MembershipGeneration = $membershipGeneration
                  AND RetiredAtUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$reconciledThroughSequence", newCursor);
            command.Parameters.AddWithValue("$subscriptionId", subscription.SubscriptionId);
            command.Parameters.AddWithValue("$membershipGeneration", membershipGeneration);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return hasMore;
    }

    internal AgentLifecycleDeliveryClaim? TryClaimLifecycleDelivery(
        string subscriptionId,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureRuntimeGenerationCurrent(connection, transaction);
        long eventSequence;
        long deliveryVersion;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT delivery.EventSequence, delivery.DeliveryVersion
                FROM AgentLifecycleDeliveries delivery
                INNER JOIN AgentLifecycleOutbox candidate ON candidate.Sequence = delivery.EventSequence
                INNER JOIN AgentLifecycleSubscriptions subscription
                    ON subscription.SubscriptionId = delivery.SubscriptionId
                WHERE delivery.SubscriptionId = $subscriptionId
                  AND delivery.EventSequence <= subscription.ReconciledThroughSequence
                  AND subscription.RetiredAtUtc IS NULL
                  AND delivery.MembershipGeneration = subscription.MembershipGeneration
                  AND ((delivery.Status IN ('Pending', 'Poison') AND delivery.NextAttemptAtUtc <= $now)
                       OR (delivery.Status = 'InFlight'
                           AND (delivery.LeaseExpiresAtUtc IS NULL OR delivery.LeaseExpiresAtUtc <= $now)))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentLifecycleDeliveries earlier
                      INNER JOIN AgentLifecycleOutbox earlierEvent ON earlierEvent.Sequence = earlier.EventSequence
                      WHERE earlier.SubscriptionId = delivery.SubscriptionId
                        AND earlier.MembershipGeneration = subscription.MembershipGeneration
                        AND earlier.EventSequence < delivery.EventSequence
                        AND (earlierEvent.OrderingKey = candidate.OrderingKey
                             OR (candidate.WorkspaceId IS NOT NULL
                                 AND earlierEvent.WorkspaceId = candidate.WorkspaceId
                                 AND instr(candidate.OrderingKey, ':root:') > 0
                                  AND earlierEvent.OrderingKey =
                                      'workspace:' || candidate.WorkspaceId
                                      || substr(candidate.OrderingKey, instr(candidate.OrderingKey, ':root:')))
                             OR (candidate.EventType = 'WorkspaceDeleted'
                                 AND earlierEvent.WorkspaceId = candidate.WorkspaceId
                                 AND (earlierEvent.OrderingKey = candidate.OrderingKey
                                      OR substr(earlierEvent.OrderingKey, 1, length(candidate.OrderingKey) + 1)
                                         = candidate.OrderingKey || ':'
                                      OR substr(
                                             earlierEvent.OrderingKey,
                                             1,
                                             length('workspace:' || candidate.WorkspaceId || ':root:'))
                                         = 'workspace:' || candidate.WorkspaceId || ':root:')))
                        AND earlier.Status IN ('Pending', 'InFlight', 'Poison'))
                ORDER BY delivery.EventSequence
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$subscriptionId", subscriptionId);
            select.Parameters.AddWithValue("$now", now.ToString("O"));
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return null;
            }

            eventSequence = reader.GetInt64(0);
            deliveryVersion = reader.GetInt64(1);
        }

        var leaseToken = Guid.NewGuid().ToString("N");
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE AgentLifecycleDeliveries
                SET Status = 'InFlight',
                    LeaseToken = $leaseToken,
                    LeaseExpiresAtUtc = $leaseExpiresAtUtc,
                    LastAttemptAtUtc = $now
                WHERE SubscriptionId = $subscriptionId
                  AND EventSequence = $eventSequence
                  AND DeliveryVersion = $deliveryVersion
                  AND ((Status IN ('Pending', 'Poison') AND NextAttemptAtUtc <= $now)
                       OR (Status = 'InFlight' AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc <= $now)));
                """;
            update.Parameters.AddWithValue("$leaseToken", leaseToken);
            update.Parameters.AddWithValue("$leaseExpiresAtUtc", now.Add(leaseDuration).ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$subscriptionId", subscriptionId);
            update.Parameters.AddWithValue("$eventSequence", eventSequence);
            update.Parameters.AddWithValue("$deliveryVersion", deliveryVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        AgentLifecycleOutboxRecord outboxEvent;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {LifecycleOutboxColumns} FROM AgentLifecycleOutbox WHERE Sequence = $sequence;";
            select.Parameters.AddWithValue("$sequence", eventSequence);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Lifecycle outbox event '{eventSequence}' was not found.");
            }
            outboxEvent = ReadLifecycleOutboxRecord(reader);
        }

        transaction.Commit();
        return new AgentLifecycleDeliveryClaim(subscriptionId, leaseToken, deliveryVersion, outboxEvent);
    }

    private static long AdvanceLifecycleGeneration(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentLifecycleGenerationState
            SET CurrentGeneration = CurrentGeneration + 1
            WHERE SingletonId = 1;
            SELECT CurrentGeneration
            FROM AgentLifecycleGenerationState
            WHERE SingletonId = 1;
            """;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

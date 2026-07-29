using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private AgentMemoryConsistencyBarrier EnqueueRollbackLifecycleEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSessionRecord session,
        AgentTranscriptRollbackResult rollback)
    {
        if (rollback.DeletedSessionIds.Count > 0)
        {
            var erasedAtUtc = DateTimeOffset.UtcNow;
            var erasureGeneration = AdvanceLifecycleGeneration(connection, transaction);
            EraseLifecyclePayloadsForSessions(
                connection,
                transaction,
                rollback.DeletedSessionIds,
                erasedAtUtc,
                erasureGeneration);
            RearmLifecycleDeliveriesForErasure(
                connection,
                transaction,
                erasureGeneration,
                erasedAtUtc);
        }
        var workspaceIncarnationId = ReadWorkspaceIncarnationId(
            connection,
            transaction,
            session.WorkspaceId);
        var payload = new AgentDurableLifecycleEventPayload
        {
            SessionId = rollback.SessionId,
            RootSessionId = session.RootSessionId ?? session.SessionId,
            WorkspaceId = session.WorkspaceId,
            WorkspaceIncarnationId = workspaceIncarnationId,
            DeletedTurnIds = rollback.DeletedTurnIds,
            DeletedChildSessionIds = rollback.DeletedSessionIds,
            DeletedSessionIds = rollback.DeletedSessionIds,
        };
        return EnqueueDeletionLifecyclePayload(
            connection,
            transaction,
            AgentLifecycleEventKind.TranscriptRolledBack,
            $"transcript-rollback:{rollback.SessionId:N}:{rollback.AnchorTurnId:N}",
            BuildOrderingKey(session.WorkspaceId, workspaceIncarnationId, session.RootSessionId ?? session.SessionId),
            session.WorkspaceId,
            session.SessionId,
            payload);
    }

    private AgentMemoryConsistencyBarrier EnqueueSessionDeletedLifecycleEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSessionRecord rootSession,
        IReadOnlyList<Guid> deletedSessionIds)
    {
        var erasedAtUtc = DateTimeOffset.UtcNow;
        var erasureGeneration = AdvanceLifecycleGeneration(connection, transaction);
        EraseLifecyclePayloadsForSessions(
            connection,
            transaction,
            deletedSessionIds,
            erasedAtUtc,
            erasureGeneration);
        RearmLifecycleDeliveriesForErasure(
            connection,
            transaction,
            erasureGeneration,
            erasedAtUtc);
        var workspaceIncarnationId = ReadWorkspaceIncarnationId(
            connection,
            transaction,
            rootSession.WorkspaceId);
        var payload = new AgentDurableLifecycleEventPayload
        {
            SessionId = rootSession.SessionId,
            RootSessionId = rootSession.RootSessionId ?? rootSession.SessionId,
            WorkspaceId = rootSession.WorkspaceId,
            WorkspaceIncarnationId = workspaceIncarnationId,
            DeletedChildSessionIds = deletedSessionIds.Where(id => id != rootSession.SessionId).ToArray(),
            DeletedSessionIds = deletedSessionIds,
        };
        return EnqueueDeletionLifecyclePayload(
            connection,
            transaction,
            AgentLifecycleEventKind.SessionDeleted,
            $"session-delete:{rootSession.SessionId:N}",
            BuildOrderingKey(rootSession.WorkspaceId, workspaceIncarnationId, rootSession.RootSessionId ?? rootSession.SessionId),
            rootSession.WorkspaceId,
            rootSession.SessionId,
            payload);
    }

    private AgentMemoryConsistencyBarrier EnqueueWorkspaceDeletedLifecycleEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        string workspaceIncarnationId,
        IReadOnlyList<Guid> deletedSessionIds)
    {
        var erasedAtUtc = DateTimeOffset.UtcNow;
        var erasureGeneration = AdvanceLifecycleGeneration(connection, transaction);
        EraseLifecyclePayloadsForSessions(
            connection,
            transaction,
            deletedSessionIds,
            erasedAtUtc,
            erasureGeneration);
        EraseLifecyclePayloadsForWorkspace(
            connection,
            transaction,
            workspaceId,
            workspaceIncarnationId,
            erasedAtUtc,
            erasureGeneration);
        RearmLifecycleDeliveriesForErasure(
            connection,
            transaction,
            erasureGeneration,
            erasedAtUtc);
        var payload = new AgentDurableLifecycleEventPayload
        {
            WorkspaceId = workspaceId,
            WorkspaceIncarnationId = workspaceIncarnationId,
            DeletedSessionIds = deletedSessionIds,
        };
        return EnqueueDeletionLifecyclePayload(
            connection,
            transaction,
            AgentLifecycleEventKind.WorkspaceDeleted,
            $"workspace-delete:{workspaceIncarnationId}",
            $"workspace:{workspaceIncarnationId}",
            workspaceId,
            sessionId: null,
            payload);
    }

    private AgentMemoryConsistencyBarrier EnqueueDeletionLifecyclePayload(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentLifecycleEventKind kind,
        string sourceKey,
        string orderingKey,
        string? workspaceId,
        Guid? sessionId,
        AgentDurableLifecycleEventPayload payload)
    {
        if (payload.DeletedTurnIds.Count <= MaxLifecycleDeletionIdsPerChunk
            && payload.DeletedChildSessionIds.Count <= MaxLifecycleDeletionIdsPerChunk
            && payload.DeletedSessionIds.Count <= MaxLifecycleDeletionIdsPerChunk)
        {
            return EnqueueLifecyclePayload(
                connection,
                transaction,
                kind,
                sourceKey,
                orderingKey,
                workspaceId,
                sessionId,
                payload);
        }

        var identityCount = checked(
            payload.DeletedTurnIds.Count
            + payload.DeletedChildSessionIds.Count
            + payload.DeletedSessionIds.Count);
        if (identityCount > MaxLifecycleDeletionIds)
        {
            throw new InvalidOperationException(
                $"A durable lifecycle deletion cannot contain more than {MaxLifecycleDeletionIds} exact identifiers.");
        }

        var chunkCount = new[]
        {
            GetLifecycleDeletionChunkCount(payload.DeletedTurnIds.Count),
            GetLifecycleDeletionChunkCount(payload.DeletedChildSessionIds.Count),
            GetLifecycleDeletionChunkCount(payload.DeletedSessionIds.Count),
        }.Max();
        var batchId = BuildLifecycleEventId(sourceKey + ":batch");
        var receipts = new List<AgentMemoryConsistencyBarrier>(chunkCount);
        for (var index = 0; index < chunkCount; index++)
        {
            var chunk = payload with
            {
                DeletedTurnIds = SliceLifecycleDeletionIds(payload.DeletedTurnIds, index),
                DeletedChildSessionIds = SliceLifecycleDeletionIds(payload.DeletedChildSessionIds, index),
                DeletedSessionIds = SliceLifecycleDeletionIds(payload.DeletedSessionIds, index),
                DeletionBatchId = batchId,
                DeletionChunkIndex = index,
                DeletionChunkCount = chunkCount,
                DeletedTurnCount = payload.DeletedTurnIds.Count,
                DeletedChildSessionCount = payload.DeletedChildSessionIds.Count,
                DeletedSessionCount = payload.DeletedSessionIds.Count,
                DeletionChunkReceipts = [],
            };
            receipts.Add(EnqueueLifecyclePayload(
                connection,
                transaction,
                kind,
                $"{sourceKey}:chunk:{index:D6}-of-{chunkCount:D6}",
                orderingKey,
                workspaceId,
                sessionId,
                chunk));
        }

        var manifest = payload with
        {
            DeletedTurnIds = [],
            DeletedChildSessionIds = [],
            DeletedSessionIds = [],
            DeletionBatchId = batchId,
            DeletionChunkIndex = null,
            DeletionChunkCount = chunkCount,
            DeletedTurnCount = payload.DeletedTurnIds.Count,
            DeletedChildSessionCount = payload.DeletedChildSessionIds.Count,
            DeletedSessionCount = payload.DeletedSessionIds.Count,
            DeletionChunkReceipts = receipts,
        };
        return EnqueueLifecyclePayload(
            connection,
            transaction,
            kind,
            sourceKey + ":manifest",
            orderingKey,
            workspaceId,
            sessionId,
            manifest);
    }

    private static int GetLifecycleDeletionChunkCount(int count)
        => (count + MaxLifecycleDeletionIdsPerChunk - 1) / MaxLifecycleDeletionIdsPerChunk;

    private static IReadOnlyList<Guid> SliceLifecycleDeletionIds(IReadOnlyList<Guid> ids, int chunkIndex)
        => ids.Skip(chunkIndex * MaxLifecycleDeletionIdsPerChunk)
            .Take(MaxLifecycleDeletionIdsPerChunk)
            .ToArray();

    private static void EraseLifecyclePayloadsForSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> sessionIds,
        DateTimeOffset erasedAtUtc,
        long erasureGeneration)
    {
        foreach (var sessionIdChunk in sessionIds.Distinct().Chunk(MaxLifecycleDeletionIdsPerChunk))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var parameterNames = new string[sessionIdChunk.Length];
            for (var index = 0; index < sessionIdChunk.Length; index++)
            {
                parameterNames[index] = $"$sessionId{index}";
                command.Parameters.AddWithValue(parameterNames[index], sessionIdChunk[index].ToString());
            }
            command.CommandText = $"""
                UPDATE AgentLifecycleOutbox
                SET PayloadJson = $payloadJson,
                    PayloadHash = $payloadHash,
                     PayloadState = 'Erased',
                     OriginalPayloadHash = PayloadHash,
                     PayloadErasedAtUtc = $erasedAtUtc,
                     PayloadErasureGeneration = $erasureGeneration
                WHERE PayloadState = 'Available'
                  AND SessionId IN ({string.Join(", ", parameterNames)});
                """;
            command.Parameters.AddWithValue("$payloadJson", ErasedLifecyclePayloadJson);
            command.Parameters.AddWithValue("$payloadHash", ErasedLifecyclePayloadHash);
            command.Parameters.AddWithValue("$erasedAtUtc", erasedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$erasureGeneration", erasureGeneration);
            command.ExecuteNonQuery();
        }
    }

    private static void EraseLifecyclePayloadsForWorkspace(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        string workspaceIncarnationId,
        DateTimeOffset erasedAtUtc,
        long erasureGeneration)
    {
        var orderingPrefix = $"workspace:{workspaceIncarnationId}";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AgentLifecycleOutbox
            SET PayloadJson = $payloadJson,
                PayloadHash = $payloadHash,
                 PayloadState = 'Erased',
                 OriginalPayloadHash = PayloadHash,
                 PayloadErasedAtUtc = $erasedAtUtc,
                 PayloadErasureGeneration = $erasureGeneration
            WHERE PayloadState = 'Available'
              AND WorkspaceId = $workspaceId
              AND (OrderingKey = $orderingPrefix
                   OR substr(OrderingKey, 1, length($orderingPrefix) + 1) = $orderingPrefix || ':');
            """;
        command.Parameters.AddWithValue("$payloadJson", ErasedLifecyclePayloadJson);
        command.Parameters.AddWithValue("$payloadHash", ErasedLifecyclePayloadHash);
        command.Parameters.AddWithValue("$erasedAtUtc", erasedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$erasureGeneration", erasureGeneration);
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$orderingPrefix", orderingPrefix);
        command.ExecuteNonQuery();
    }

    private static void RearmLifecycleDeliveriesForErasure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long erasureGeneration,
        DateTimeOffset now)
    {
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE AgentLifecycleDeliveries AS delivery
                SET Status = CASE WHEN delivery.Status = 'Poison' THEN 'Poison' ELSE 'Pending' END,
                    AttemptCount = CASE WHEN delivery.Status = 'Poison' THEN delivery.AttemptCount ELSE 0 END,
                    NextAttemptAtUtc = CASE WHEN delivery.Status = 'Poison' THEN delivery.NextAttemptAtUtc ELSE $now END,
                    LeaseToken = NULL,
                    LeaseExpiresAtUtc = NULL,
                    LastAttemptAtUtc = CASE WHEN delivery.Status = 'Poison' THEN delivery.LastAttemptAtUtc ELSE NULL END,
                    DeliveredAtUtc = NULL,
                    PoisonedAtUtc = CASE WHEN delivery.Status = 'Poison' THEN delivery.PoisonedAtUtc ELSE NULL END,
                    LastError = CASE WHEN delivery.Status = 'Poison' THEN delivery.LastError ELSE NULL END,
                    DeliveryVersion = delivery.DeliveryVersion + 1
                WHERE EXISTS (
                    SELECT 1
                    FROM AgentLifecycleOutbox outbox
                    INNER JOIN AgentLifecycleSubscriptions subscription
                        ON subscription.SubscriptionId = delivery.SubscriptionId
                    WHERE outbox.Sequence = delivery.EventSequence
                      AND outbox.PayloadErasureGeneration = $erasureGeneration
                      AND subscription.RetiredAtUtc IS NULL
                      AND subscription.MembershipGeneration <= $erasureGeneration
                      AND delivery.MembershipGeneration = subscription.MembershipGeneration
                      AND (subscription.ContractKind = 'Durable'
                           OR outbox.EventType IN (
                               'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                               'RunInterrupted', 'RunStopped', 'RunFailed'))
                );
                """;
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$erasureGeneration", erasureGeneration);
            update.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO AgentLifecycleDeliveries (
                SubscriptionId, EventSequence, Status, AttemptCount, NextAttemptAtUtc,
                MembershipGeneration, DeliveryVersion)
            SELECT
                subscription.SubscriptionId,
                outbox.Sequence,
                'Pending',
                0,
                $now,
                subscription.MembershipGeneration,
                1
            FROM AgentLifecycleOutbox outbox
            CROSS JOIN AgentLifecycleSubscriptions subscription
            WHERE outbox.PayloadErasureGeneration = $erasureGeneration
              AND subscription.RetiredAtUtc IS NULL
              AND subscription.MembershipGeneration <= $erasureGeneration
              AND (subscription.ContractKind = 'Durable'
                   OR outbox.EventType IN (
                       'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                       'RunInterrupted', 'RunStopped', 'RunFailed'));
            """;
        insert.Parameters.AddWithValue("$now", now.ToString("O"));
        insert.Parameters.AddWithValue("$erasureGeneration", erasureGeneration);
        insert.ExecuteNonQuery();
    }
}

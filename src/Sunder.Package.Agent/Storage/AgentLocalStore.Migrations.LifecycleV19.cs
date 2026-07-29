using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const string LifecycleDurabilityHardeningSql = """
            DROP TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate;
            DROP TRIGGER TR_AgentLifecycleOutbox_ImmutableDelete;

            ALTER TABLE AgentLifecycleOutbox RENAME TO AgentLifecycleOutboxV18;

            CREATE TABLE AgentLifecycleOutbox (
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE CHECK (trim(EventId) <> '' AND length(EventId) <= 80),
                SourceKey TEXT NOT NULL UNIQUE CHECK (trim(SourceKey) <> '' AND length(SourceKey) <= 1024),
                EventType TEXT NOT NULL CHECK (EventType IN (
                    'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                    'RunInterrupted', 'RunStopped', 'RunFailed', 'TranscriptRolledBack',
                    'SessionDeleted', 'WorkspaceDeleted')),
                OrderingKey TEXT NOT NULL CHECK (trim(OrderingKey) <> '' AND length(OrderingKey) <= 1024),
                WorkspaceId TEXT NULL CHECK (WorkspaceId IS NULL OR length(WorkspaceId) <= 512),
                SessionId TEXT NULL,
                PayloadJson TEXT NOT NULL CHECK (length(PayloadJson) <= 262144),
                PayloadHash TEXT NOT NULL CHECK (length(PayloadHash) = 64),
                CreatedAtUtc TEXT NOT NULL,
                PayloadState TEXT NOT NULL DEFAULT 'Available' CHECK (PayloadState IN ('Available', 'Erased')),
                OriginalPayloadHash TEXT NULL CHECK (OriginalPayloadHash IS NULL OR length(OriginalPayloadHash) = 64),
                PayloadErasedAtUtc TEXT NULL,
                PayloadErasureGeneration INTEGER NULL CHECK (
                    PayloadErasureGeneration IS NULL OR PayloadErasureGeneration >= 1),
                CHECK (
                    (PayloadState = 'Available'
                     AND OriginalPayloadHash IS NULL
                     AND PayloadErasedAtUtc IS NULL
                     AND PayloadErasureGeneration IS NULL
                     AND PayloadJson <> '{"contentErased":true}')
                    OR
                    (PayloadState = 'Erased'
                     AND OriginalPayloadHash IS NOT NULL
                     AND PayloadErasedAtUtc IS NOT NULL
                     AND PayloadErasureGeneration IS NOT NULL
                     AND PayloadJson = '{"contentErased":true}'
                     AND PayloadHash = '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950')
                )
            );

            INSERT INTO AgentLifecycleOutbox (
                Sequence, EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                PayloadJson, PayloadHash, CreatedAtUtc, PayloadState, OriginalPayloadHash,
                PayloadErasedAtUtc, PayloadErasureGeneration)
            SELECT
                Sequence,
                EventId,
                SourceKey,
                EventType,
                OrderingKey,
                WorkspaceId,
                SessionId,
                CASE
                    WHEN PayloadState = 'Erased' OR PayloadJson = '{"contentErased":true}'
                        THEN '{"contentErased":true}'
                    ELSE PayloadJson
                END,
                CASE
                    WHEN PayloadState = 'Erased' OR PayloadJson = '{"contentErased":true}'
                        THEN '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950'
                    ELSE PayloadHash
                END,
                CreatedAtUtc,
                CASE
                    WHEN PayloadState = 'Erased' OR PayloadJson = '{"contentErased":true}'
                        THEN 'Erased'
                    ELSE 'Available'
                END,
                CASE
                    WHEN PayloadState = 'Erased' OR PayloadJson = '{"contentErased":true}'
                        THEN CASE
                            WHEN length(OriginalPayloadHash) = 64 THEN OriginalPayloadHash
                            WHEN PayloadHash <> '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950'
                                THEN PayloadHash
                            ELSE '0000000000000000000000000000000000000000000000000000000000000000'
                        END
                    ELSE NULL
                END,
                CASE
                    WHEN PayloadState = 'Erased' OR PayloadJson = '{"contentErased":true}'
                        THEN COALESCE(PayloadErasedAtUtc, $migrationNow)
                    ELSE NULL
                END,
                CASE
                    WHEN PayloadState = 'Erased' OR PayloadJson = '{"contentErased":true}' THEN 2
                    ELSE NULL
                END
            FROM AgentLifecycleOutboxV18;

            DROP TABLE AgentLifecycleOutboxV18;

            CREATE INDEX IX_AgentLifecycleOutbox_Ordering
                ON AgentLifecycleOutbox (OrderingKey, Sequence);
            CREATE INDEX IX_AgentLifecycleOutbox_Replay
                ON AgentLifecycleOutbox (PayloadState, Sequence);
            CREATE INDEX IX_AgentLifecycleOutbox_ErasureGeneration
                ON AgentLifecycleOutbox (PayloadErasureGeneration, Sequence)
                WHERE PayloadState = 'Erased';

            ALTER TABLE AgentLifecycleSubscriptions ADD COLUMN MembershipGeneration INTEGER NOT NULL DEFAULT 1
                CHECK (MembershipGeneration >= 1);
            ALTER TABLE AgentLifecycleSubscriptions ADD COLUMN RetiredAtUtc TEXT NULL;

            ALTER TABLE AgentLifecycleDeliveries ADD COLUMN MembershipGeneration INTEGER NOT NULL DEFAULT 1
                CHECK (MembershipGeneration >= 1);
            ALTER TABLE AgentLifecycleDeliveries ADD COLUMN DeliveryVersion INTEGER NOT NULL DEFAULT 1
                CHECK (DeliveryVersion >= 1);

            CREATE TABLE AgentLifecycleGenerationState (
                SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
                CurrentGeneration INTEGER NOT NULL CHECK (CurrentGeneration >= 1)
            );
            INSERT INTO AgentLifecycleGenerationState (SingletonId, CurrentGeneration) VALUES (1, 2);

            CREATE TABLE AgentSessionDataCleaners (
                PackageId TEXT NOT NULL CHECK (trim(PackageId) <> '' AND length(PackageId) <= 256),
                CleanerId TEXT NOT NULL CHECK (trim(CleanerId) <> '' AND length(CleanerId) <= 512),
                LastSeenAtUtc TEXT NOT NULL,
                PRIMARY KEY (PackageId, CleanerId)
            );

            CREATE TABLE AgentSessionCleanupJobs (
                JobId INTEGER PRIMARY KEY AUTOINCREMENT,
                PackageId TEXT NOT NULL CHECK (trim(PackageId) <> '' AND length(PackageId) <= 256),
                CleanerId TEXT NOT NULL CHECK (trim(CleanerId) <> '' AND length(CleanerId) <= 512),
                SessionId TEXT NOT NULL CHECK (trim(SessionId) <> ''),
                Status TEXT NOT NULL CHECK (Status IN ('Pending', 'InFlight', 'Completed')),
                AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
                NextAttemptAtUtc TEXT NOT NULL,
                LeaseToken TEXT NULL,
                LeaseExpiresAtUtc TEXT NULL,
                LastAttemptAtUtc TEXT NULL,
                CompletedAtUtc TEXT NULL,
                LastError TEXT NULL CHECK (LastError IS NULL OR length(LastError) <= 1024),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE (PackageId, CleanerId, SessionId)
            );

            CREATE INDEX IX_AgentSessionCleanupJobs_Dispatch
                ON AgentSessionCleanupJobs (PackageId, CleanerId, Status, NextAttemptAtUtc, JobId);
            CREATE INDEX IX_AgentSessionCleanupJobs_Recovery
                ON AgentSessionCleanupJobs (Status, LeaseExpiresAtUtc)
                WHERE Status = 'InFlight';

            CREATE TABLE AgentLifecycleMaintenanceState (
                SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
                AllowCompaction INTEGER NOT NULL DEFAULT 0 CHECK (AllowCompaction IN (0, 1))
            );
            INSERT INTO AgentLifecycleMaintenanceState (SingletonId, AllowCompaction) VALUES (1, 0);

            CREATE TRIGGER TR_AgentLifecycleOutbox_CanonicalInsert
            BEFORE INSERT ON AgentLifecycleOutbox
            WHEN NEW.PayloadState <> 'Available'
                 OR NEW.OriginalPayloadHash IS NOT NULL
                 OR NEW.PayloadErasedAtUtc IS NOT NULL
                 OR NEW.PayloadErasureGeneration IS NOT NULL
                 OR NEW.PayloadJson = '{"contentErased":true}'
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events must be inserted with available payloads');
            END;

            CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate
            BEFORE UPDATE ON AgentLifecycleOutbox
            WHEN NOT (
                OLD.PayloadState = 'Available'
                AND OLD.OriginalPayloadHash IS NULL
                AND OLD.PayloadErasedAtUtc IS NULL
                AND OLD.PayloadErasureGeneration IS NULL
                AND NEW.PayloadState = 'Erased'
                AND NEW.OriginalPayloadHash = OLD.PayloadHash
                AND NEW.PayloadErasedAtUtc IS NOT NULL
                AND NEW.PayloadErasureGeneration = (
                    SELECT CurrentGeneration FROM AgentLifecycleGenerationState WHERE SingletonId = 1)
                AND NEW.PayloadJson = '{"contentErased":true}'
                AND NEW.PayloadHash = '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950'
                AND NEW.Sequence IS OLD.Sequence
                AND NEW.EventId IS OLD.EventId
                AND NEW.SourceKey IS OLD.SourceKey
                AND NEW.EventType IS OLD.EventType
                AND NEW.OrderingKey IS OLD.OrderingKey
                AND NEW.WorkspaceId IS OLD.WorkspaceId
                AND NEW.SessionId IS OLD.SessionId
                AND NEW.CreatedAtUtc IS OLD.CreatedAtUtc
            )
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events are immutable except for canonical content erasure');
            END;

            CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableDelete
            BEFORE DELETE ON AgentLifecycleOutbox
            WHEN (SELECT AllowCompaction FROM AgentLifecycleMaintenanceState WHERE SingletonId = 1) IS NOT 1
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events may only be deleted by bounded compaction');
            END;

            UPDATE AgentLifecycleOutbox
            SET PayloadJson = '{"contentErased":true}',
                PayloadHash = '2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950',
                PayloadState = 'Erased',
                OriginalPayloadHash = PayloadHash,
                PayloadErasedAtUtc = $migrationNow,
                PayloadErasureGeneration = 2
            WHERE PayloadState = 'Available'
              AND EventType NOT IN ('SessionDeleted', 'WorkspaceDeleted')
              AND (
                  (SessionId IS NOT NULL AND NOT EXISTS (
                      SELECT 1 FROM AgentSessions session WHERE session.SessionId = AgentLifecycleOutbox.SessionId))
                  OR
                  (WorkspaceId IS NOT NULL AND NOT EXISTS (
                      SELECT 1 FROM AgentWorkspaces workspace WHERE workspace.WorkspaceId = AgentLifecycleOutbox.WorkspaceId))
              );

            UPDATE AgentLifecycleDeliveries AS delivery
            SET Status = CASE WHEN delivery.Status = 'Poison' THEN 'Poison' ELSE 'Pending' END,
                AttemptCount = CASE WHEN delivery.Status = 'Poison' THEN delivery.AttemptCount ELSE 0 END,
                NextAttemptAtUtc = CASE WHEN delivery.Status = 'Poison' THEN delivery.NextAttemptAtUtc ELSE $migrationNow END,
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
                  AND outbox.PayloadState = 'Erased'
                  AND subscription.RetiredAtUtc IS NULL
                  AND subscription.MembershipGeneration <= outbox.PayloadErasureGeneration
                  AND delivery.MembershipGeneration = subscription.MembershipGeneration
                  AND (subscription.ContractKind = 'Durable'
                       OR outbox.EventType IN (
                           'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                           'RunInterrupted', 'RunStopped', 'RunFailed'))
            );

            INSERT OR IGNORE INTO AgentLifecycleDeliveries (
                SubscriptionId, EventSequence, Status, AttemptCount, NextAttemptAtUtc,
                MembershipGeneration, DeliveryVersion)
            SELECT
                subscription.SubscriptionId,
                outbox.Sequence,
                'Pending',
                0,
                $migrationNow,
                subscription.MembershipGeneration,
                1
            FROM AgentLifecycleOutbox outbox
            CROSS JOIN AgentLifecycleSubscriptions subscription
            WHERE outbox.PayloadState = 'Erased'
              AND subscription.RetiredAtUtc IS NULL
              AND subscription.MembershipGeneration <= outbox.PayloadErasureGeneration
              AND (subscription.ContractKind = 'Durable'
                   OR outbox.EventType IN (
                       'UserTurnAdded', 'AssistantTurnCompleted', 'ToolResultRecorded',
                       'RunInterrupted', 'RunStopped', 'RunFailed'));
        """;

    private static void ApplyLifecycleDurabilityHardening(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LifecycleDurabilityHardeningSql;
        command.Parameters.AddWithValue("$migrationNow", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}

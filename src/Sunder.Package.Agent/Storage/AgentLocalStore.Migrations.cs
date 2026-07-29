using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static readonly SchemaMigration[] SchemaMigrations =
    [
        new(
            1,
            "legacy-schema-baseline",
            "ApplyV1Baseline-v1",
            ApplyV1Baseline),
        SqlMigration(
            2,
            "agent-runs",
            """
            CREATE TABLE AgentRuns (
                RunId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                RunRevision INTEGER NOT NULL,
                Epoch INTEGER NOT NULL,
                Status TEXT NOT NULL,
                ProfileId TEXT NOT NULL,
                UserMessage TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                FinishedAtUtc TEXT NULL,
                UNIQUE (SessionId, RunRevision)
            );

            CREATE INDEX IX_AgentRuns_SessionId_CurrentRevision
                ON AgentRuns (SessionId, RunRevision DESC)
                WHERE FinishedAtUtc IS NULL;
            """),
        SqlMigration(
            3,
            "pending-permission-state",
            """
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN Status TEXT NOT NULL DEFAULT 'Pending'
                CHECK (Status IN ('Pending', 'Claimed', 'Executed', 'Denied', 'Failed', 'Expired'));
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ClaimToken TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ClaimedAtUtc TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN DecidedAtUtc TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN DecisionSummary TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ExecutionFingerprint TEXT NOT NULL DEFAULT '';

            CREATE INDEX IX_AgentPendingPermissionRequests_ActiveStatus
                ON AgentPendingPermissionRequests (SessionId, Status, CreatedAtUtc DESC);

            CREATE UNIQUE INDEX UX_AgentPendingPermissionRequests_ActiveRunCall
                ON AgentPendingPermissionRequests (SessionId, RunId, RunRevision, CallId)
                WHERE Status IN ('Pending', 'Claimed')
                  AND ExecutionFingerprint <> ''
                  AND CallId <> '';
            """),
        SqlMigration(
            4,
            "typed-run-suspensions",
            """
            ALTER TABLE AgentRuns ADD COLUMN SuspensionKind TEXT NULL
                CHECK (SuspensionKind IS NULL OR SuspensionKind IN ('Permission', 'ChildJoin'));
            ALTER TABLE AgentRuns ADD COLUMN ContinuationToken TEXT NULL;
            ALTER TABLE AgentRuns ADD COLUMN SuspensionDataJson TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ContinuationToken TEXT NULL;

            CREATE UNIQUE INDEX UX_AgentRuns_ActiveContinuationToken
                ON AgentRuns (ContinuationToken)
                WHERE ContinuationToken IS NOT NULL
                  AND FinishedAtUtc IS NULL;

            CREATE UNIQUE INDEX UX_AgentPendingPermissionRequests_ActiveContinuationToken
                ON AgentPendingPermissionRequests (ContinuationToken)
                WHERE ContinuationToken IS NOT NULL
                  AND Status IN ('Pending', 'Claimed');
            """),
        SqlMigration(
            5,
            "permission-claim-recovery",
            """
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ClaimLeaseExpiresAtUtc TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ContinuationConsumedAtUtc TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ExecutionStartedAtUtc TEXT NULL;

            CREATE INDEX IX_AgentPendingPermissionRequests_ClaimRecovery
                ON AgentPendingPermissionRequests (Status, ClaimLeaseExpiresAtUtc)
                WHERE Status = 'Claimed';
            """),
        SqlMigration(
            6,
            "parent-continuation-work",
            """
            CREATE TABLE AgentParentContinuationWork (
                WorkId TEXT PRIMARY KEY,
                ParentRunId TEXT NOT NULL,
                ParentSessionId TEXT NOT NULL,
                ParentRunRevision INTEGER NOT NULL,
                ChildSessionId TEXT NOT NULL,
                ToolCallId TEXT NOT NULL,
                ChildStatus TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Content TEXT NULL,
                Title TEXT NULL,
                SuspensionDataJson TEXT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Pending', 'Ready', 'Dispatching', 'Completed', 'Failed')),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                ExecutionStartedAtUtc TEXT NULL,
                LastError TEXT NULL,
                UNIQUE (ParentRunId, ChildSessionId, ToolCallId)
            );

            CREATE INDEX IX_AgentParentContinuationWork_Dispatch
                ON AgentParentContinuationWork (Status, UpdatedAtUtc)
                WHERE Status IN ('Pending', 'Ready', 'Dispatching');
            """),
        SqlMigration(
            7,
            "permission-execution-snapshot",
            """
            ALTER TABLE AgentPendingPermissionRequests
                ADD COLUMN ExecutionSnapshotJson TEXT NOT NULL DEFAULT '';
            """),
        SqlMigration(
            8,
            "turn-content-revisions",
            """
            ALTER TABLE AgentTurns ADD COLUMN ContentRevision INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE AgentTurns ADD COLUMN IsStreaming INTEGER NOT NULL DEFAULT 0;
            """),
        SqlMigration(
            9,
            "turn-run-ownership",
            """
            ALTER TABLE AgentTurns ADD COLUMN RunId TEXT NULL;
            ALTER TABLE AgentTurns ADD COLUMN RunRevision INTEGER NULL;

            UPDATE AgentTurns
            SET ContentRevision = ContentRevision + 1,
                IsStreaming = 0
            WHERE IsStreaming = 1
              AND RunId IS NULL;

            CREATE INDEX IX_AgentTurns_StreamingRun
                ON AgentTurns (SessionId, RunId, RunRevision)
                WHERE IsStreaming = 1;
            """),
        // Keep the provisional v10 checksum stable for local databases while fresh 1.1 baselines omit both tables.
        new(
            10,
            "remove-dormant-continuity-and-permission-schema",
            """
            INSERT INTO AgentSessionContextCheckpoints (
                ContextCheckpointId,
                SessionId,
                FirstOmittedTurnId,
                LastOmittedTurnId,
                OmittedTurnCount,
                SummaryText,
                DetailsJson,
                CreatedAtUtc)
            SELECT
                lower(hex(randomblob(16))),
                summary.SessionId,
                NULL,
                NULL,
                0,
                summary.SummaryText,
                '{"source":"legacy-working-summary"}',
                summary.UpdatedAtUtc
            FROM AgentWorkingSummaries AS summary
            WHERE trim(summary.SummaryText) <> ''
              AND NOT EXISTS (
                  SELECT 1
                  FROM AgentSessionContextCheckpoints AS checkpoint
                  WHERE checkpoint.SessionId = summary.SessionId);

            DROP TABLE AgentWorkingSummaries;
            DROP TABLE AgentPermissionRules;
            """,
            RemoveDormantContinuityAndPermissionSchema),
        SqlMigration(
            11,
            "durable-run-budgets",
            """
            ALTER TABLE AgentRuns ADD COLUMN ProviderCycleCount INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE AgentRuns ADD COLUMN ToolCallCount INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE AgentRuns ADD COLUMN SubmittedContextTokenCount INTEGER NOT NULL DEFAULT 0;
            """),
        SqlMigration(
            12,
            "tool-execution-ledger",
            """
            CREATE TABLE AgentToolExecutions (
                ExecutionId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                RunId TEXT NOT NULL,
                RunRevision INTEGER NOT NULL,
                CallId TEXT NOT NULL CHECK (trim(CallId) <> '' AND length(CallId) <= 512),
                ToolId TEXT NOT NULL CHECK (trim(ToolId) <> '' AND length(ToolId) <= 512),
                InvocationFingerprint TEXT NOT NULL CHECK (length(InvocationFingerprint) = 64),
                IsReadOnly INTEGER NOT NULL CHECK (IsReadOnly IN (0, 1)),
                Status TEXT NOT NULL CHECK (Status IN ('Prepared', 'Started', 'Completed', 'Failed', 'Ambiguous')),
                PreparedAtUtc TEXT NOT NULL,
                StartedAtUtc TEXT NULL,
                FinishedAtUtc TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                OutcomeCode TEXT NULL CHECK (OutcomeCode IS NULL OR length(OutcomeCode) <= 128),
                OutcomeSummary TEXT NULL CHECK (OutcomeSummary IS NULL OR length(OutcomeSummary) <= 2048),
                UNIQUE (RunId, RunRevision, CallId)
            );

            ALTER TABLE AgentTurnItems ADD COLUMN ToolExecutionId TEXT NULL;
            ALTER TABLE AgentPendingPermissionRequests ADD COLUMN ToolExecutionId TEXT NULL;

            CREATE INDEX IX_AgentToolExecutions_SessionRun
                ON AgentToolExecutions (SessionId, RunRevision, PreparedAtUtc);
            CREATE INDEX IX_AgentToolExecutions_OpenRun
                ON AgentToolExecutions (RunId, RunRevision, Status)
                WHERE Status IN ('Prepared', 'Started');
            CREATE INDEX IX_AgentTurnItems_ToolExecutionId
                ON AgentTurnItems (ToolExecutionId);
            CREATE UNIQUE INDEX UX_AgentTurnItems_ToolExecutionCall
                ON AgentTurnItems (ToolExecutionId)
                WHERE ToolExecutionId IS NOT NULL AND Kind = 'ToolCall';
            CREATE UNIQUE INDEX UX_AgentTurnItems_ToolExecutionResult
                ON AgentTurnItems (ToolExecutionId)
                WHERE ToolExecutionId IS NOT NULL AND Kind = 'ToolResult';
            CREATE UNIQUE INDEX UX_AgentPendingPermissionRequests_ToolExecutionId
                ON AgentPendingPermissionRequests (ToolExecutionId)
                WHERE ToolExecutionId IS NOT NULL;
            """),
        SqlMigration(
            13,
            "durable-lifecycle-outbox",
            """
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
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate
            BEFORE UPDATE ON AgentLifecycleOutbox
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events are immutable');
            END;

            CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableDelete
            BEFORE DELETE ON AgentLifecycleOutbox
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events are immutable');
            END;

            CREATE TABLE AgentLifecycleSubscriptions (
                SubscriptionId TEXT PRIMARY KEY CHECK (trim(SubscriptionId) <> '' AND length(SubscriptionId) <= 80),
                PackageId TEXT NOT NULL CHECK (trim(PackageId) <> '' AND length(PackageId) <= 256),
                ObserverId TEXT NOT NULL CHECK (trim(ObserverId) <> '' AND length(ObserverId) <= 512),
                ContractKind TEXT NOT NULL CHECK (ContractKind IN ('Durable', 'Compatibility')),
                DisplayName TEXT NOT NULL CHECK (length(DisplayName) <= 512),
                CreatedAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL,
                UNIQUE (PackageId, ObserverId, ContractKind)
            );

            CREATE TABLE AgentLifecycleDeliveries (
                SubscriptionId TEXT NOT NULL,
                EventSequence INTEGER NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Pending', 'InFlight', 'Delivered', 'Poison')),
                AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
                NextAttemptAtUtc TEXT NOT NULL,
                LeaseToken TEXT NULL,
                LeaseExpiresAtUtc TEXT NULL,
                LastAttemptAtUtc TEXT NULL,
                DeliveredAtUtc TEXT NULL,
                PoisonedAtUtc TEXT NULL,
                LastError TEXT NULL CHECK (LastError IS NULL OR length(LastError) <= 2048),
                PRIMARY KEY (SubscriptionId, EventSequence)
            );

            CREATE INDEX IX_AgentLifecycleDeliveries_Dispatch
                ON AgentLifecycleDeliveries (SubscriptionId, Status, EventSequence, NextAttemptAtUtc);
            CREATE INDEX IX_AgentLifecycleDeliveries_LeaseRecovery
                ON AgentLifecycleDeliveries (Status, LeaseExpiresAtUtc)
                WHERE Status = 'InFlight';
            CREATE INDEX IX_AgentLifecycleOutbox_Ordering
                ON AgentLifecycleOutbox (OrderingKey, Sequence);

            ALTER TABLE AgentRuns ADD COLUMN MemoryConsistencyBarrierEventId TEXT NULL;
            ALTER TABLE AgentRuns ADD COLUMN MemoryConsistencyBarrierPayloadHash TEXT NULL;
            """),
        SqlMigration(
            14,
            "durable-user-turn-admission",
            """
            ALTER TABLE AgentRuns ADD COLUMN UserTurnId TEXT NULL;
            ALTER TABLE AgentRuns ADD COLUMN WorkspaceId TEXT NULL;
            ALTER TABLE AgentRuns ADD COLUMN AdmissionKind TEXT NULL
                CHECK (AdmissionKind IS NULL OR AdmissionKind IN ('Normal', 'Rollback'));
            ALTER TABLE AgentRuns ADD COLUMN RollbackAnchorTurnId TEXT NULL;
            ALTER TABLE AgentRuns ADD COLUMN RequestFingerprint TEXT NULL
                CHECK (RequestFingerprint IS NULL OR length(RequestFingerprint) = 64);
            ALTER TABLE AgentRuns ADD COLUMN ExecutionStartedAtUtc TEXT NULL;

            CREATE UNIQUE INDEX UX_AgentRuns_UserTurnId
                ON AgentRuns (UserTurnId)
                WHERE UserTurnId IS NOT NULL;

            CREATE INDEX IX_AgentRuns_PreparingDispatch
                ON AgentRuns (Status, StartedAtUtc, SessionId, RunRevision)
                WHERE Status = 'Preparing'
                  AND FinishedAtUtc IS NULL
                  AND UserTurnId IS NOT NULL;
            """),
        SqlMigration(
            15,
            "anchored-session-context",
            """
            ALTER TABLE AgentSessions ADD COLUMN TranscriptEpoch INTEGER NOT NULL DEFAULT 1
                CHECK (TranscriptEpoch >= 1);
            ALTER TABLE AgentSessions ADD COLUMN ActiveContextCheckpointId TEXT NULL;
            ALTER TABLE AgentSessions ADD COLUMN ActiveContextGeneration INTEGER NOT NULL DEFAULT 0
                CHECK (ActiveContextGeneration >= 0);

            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN CheckpointKind TEXT NOT NULL DEFAULT 'Legacy'
                CHECK (CheckpointKind IN ('Legacy', 'Deterministic', 'ModelRefined'));
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN TranscriptEpoch INTEGER NOT NULL DEFAULT 0
                CHECK (TranscriptEpoch >= 0);
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN CoveredThroughCreatedAtUtc TEXT NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN CoveredThroughContentRevision INTEGER NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN SourceRunId TEXT NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN SourceRunRevision INTEGER NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN SourceRunEpoch INTEGER NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN Generation INTEGER NOT NULL DEFAULT 0
                CHECK (Generation >= 0);
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN PreviousContextCheckpointId TEXT NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN GeneratorVersion TEXT NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN ProviderId TEXT NULL;
            ALTER TABLE AgentSessionContextCheckpoints ADD COLUMN ModelId TEXT NULL;

            CREATE INDEX IX_AgentSessionContextCheckpoints_SessionGeneration
                ON AgentSessionContextCheckpoints (SessionId, Generation DESC);
            CREATE INDEX IX_AgentSessionContextCheckpoints_SessionAnchor
                ON AgentSessionContextCheckpoints (
                    SessionId,
                    TranscriptEpoch,
                    CoveredThroughCreatedAtUtc,
                    LastOmittedTurnId);
            """),
        SqlMigration(
            16,
            "tool-execution-provenance",
            """
            ALTER TABLE AgentToolExecutions ADD COLUMN OwnerPackageId TEXT NULL
                CHECK (OwnerPackageId IS NULL OR (trim(OwnerPackageId) <> '' AND length(OwnerPackageId) <= 256));
            ALTER TABLE AgentToolExecutions ADD COLUMN ToolSchemaId TEXT NULL
                CHECK (ToolSchemaId IS NULL OR (trim(ToolSchemaId) <> '' AND length(ToolSchemaId) <= 512));
            ALTER TABLE AgentToolExecutions ADD COLUMN ToolSchemaVersion TEXT NULL
                CHECK (ToolSchemaVersion IS NULL OR (trim(ToolSchemaVersion) <> '' AND length(ToolSchemaVersion) <= 128));
            """),
        SqlMigration(
            17,
            "lifecycle-payload-erasure-and-replay-watermarks",
            """
            ALTER TABLE AgentLifecycleOutbox ADD COLUMN PayloadState TEXT NOT NULL DEFAULT 'Available'
                CHECK (PayloadState IN ('Available', 'Erased'));
            ALTER TABLE AgentLifecycleOutbox ADD COLUMN OriginalPayloadHash TEXT NULL
                CHECK (OriginalPayloadHash IS NULL OR length(OriginalPayloadHash) = 64);
            ALTER TABLE AgentLifecycleOutbox ADD COLUMN PayloadErasedAtUtc TEXT NULL;

            ALTER TABLE AgentLifecycleSubscriptions ADD COLUMN ReplayStartSequence INTEGER NOT NULL DEFAULT 1
                CHECK (ReplayStartSequence >= 1);
            ALTER TABLE AgentLifecycleSubscriptions ADD COLUMN ReconciledThroughSequence INTEGER NOT NULL DEFAULT 0
                CHECK (ReconciledThroughSequence >= 0);

            DROP TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate;
            CREATE TRIGGER TR_AgentLifecycleOutbox_ImmutableUpdate
            BEFORE UPDATE ON AgentLifecycleOutbox
            WHEN NOT (
                OLD.PayloadState = 'Available'
                AND OLD.OriginalPayloadHash IS NULL
                AND OLD.PayloadErasedAtUtc IS NULL
                AND NEW.PayloadState = 'Erased'
                AND NEW.OriginalPayloadHash = OLD.PayloadHash
                AND NEW.PayloadErasedAtUtc IS NOT NULL
                AND NEW.PayloadJson = '{"contentErased":true}'
                AND length(NEW.PayloadHash) = 64
                AND NEW.Sequence = OLD.Sequence
                AND NEW.EventId = OLD.EventId
                AND NEW.SourceKey = OLD.SourceKey
                AND NEW.EventType = OLD.EventType
                AND NEW.OrderingKey = OLD.OrderingKey
                AND NEW.WorkspaceId IS OLD.WorkspaceId
                AND NEW.SessionId IS OLD.SessionId
                AND NEW.CreatedAtUtc = OLD.CreatedAtUtc
            )
            BEGIN
                SELECT RAISE(ABORT, 'Agent lifecycle outbox events are immutable except for content erasure');
            END;

            UPDATE AgentLifecycleSubscriptions
            SET ReplayStartSequence = COALESCE(
                    (
                        SELECT MIN(delivery.EventSequence)
                        FROM AgentLifecycleDeliveries delivery
                        WHERE delivery.SubscriptionId = AgentLifecycleSubscriptions.SubscriptionId
                    ),
                    COALESCE((SELECT MAX(Sequence) + 1 FROM AgentLifecycleOutbox), 1)),
                ReconciledThroughSequence = COALESCE((SELECT MAX(Sequence) FROM AgentLifecycleOutbox), 0);

            UPDATE AgentLifecycleDeliveries
            SET LastError = 'legacy_observer_failure (redacted)'
            WHERE LastError IS NOT NULL;

            CREATE INDEX IX_AgentLifecycleOutbox_Replay
                ON AgentLifecycleOutbox (PayloadState, Sequence);
            """),
        SqlMigration(
            18,
            "runtime-generation-ownership",
            """
            CREATE TABLE AgentRuntimeGenerations (
                Epoch TEXT PRIMARY KEY,
                RuntimeSessionGeneration INTEGER NOT NULL CHECK (RuntimeSessionGeneration >= 1),
                ProcessId INTEGER NOT NULL CHECK (ProcessId > 0),
                ProcessStartedAtUtc TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Committed', 'Stopped', 'Dead', 'Expired')),
                CreatedAtUtc TEXT NOT NULL,
                CommittedAtUtc TEXT NOT NULL,
                LastHeartbeatAtUtc TEXT NOT NULL,
                LeaseExpiresAtUtc TEXT NOT NULL,
                StoppedAtUtc TEXT NULL
            );

            CREATE TABLE AgentRuntimeGenerationState (
                SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
                CurrentEpoch TEXT NULL
            );

            INSERT INTO AgentRuntimeGenerationState (SingletonId, CurrentEpoch)
            VALUES (1, NULL);
            """),
        new(
            19,
            "lifecycle-durability-hardening",
            LifecycleDurabilityHardeningSql,
            ApplyLifecycleDurabilityHardening),
        SqlMigration(
            20,
            "durable-resource-claims",
            """
            ALTER TABLE AgentPendingPermissionRequests
                ADD COLUMN ResourceClaimSetVersion INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE AgentPendingPermissionRequests
                ADD COLUMN ResourceClaimsJson TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE AgentToolExecutions
                ADD COLUMN ExecutionTargetOwnerPackageId TEXT NULL
                CHECK (ExecutionTargetOwnerPackageId IS NULL OR (trim(ExecutionTargetOwnerPackageId) <> '' AND length(ExecutionTargetOwnerPackageId) <= 256));

            UPDATE AgentPendingPermissionRequests
            SET ResourceClaimSetVersion = -1
            WHERE ResourceReference LIKE 'local-resource-v3:%'
               OR ResourceReference LIKE 'docker-resource-v3:%';
            """),
        new(
            21,
            "legacy-tool-execution-identities",
            LegacyToolExecutionIdentityBackfillV21,
            ApplyLegacyToolExecutionIdentityBackfill),
    ];

    private void ApplySchemaMigrations()
    {
        using var connection = CreateConnection();
        connection.Open();
        ApplySchemaMigrations(connection, SchemaMigrations[^1].Version);
    }

    private static void RemoveDormantContinuityAndPermissionSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (TableExists(connection, transaction, "AgentWorkingSummaries"))
        {
            using var workingSummaryCommand = connection.CreateCommand();
            workingSummaryCommand.Transaction = transaction;
            workingSummaryCommand.CommandText = """
                INSERT INTO AgentSessionContextCheckpoints (
                    ContextCheckpointId,
                    SessionId,
                    FirstOmittedTurnId,
                    LastOmittedTurnId,
                    OmittedTurnCount,
                    SummaryText,
                    DetailsJson,
                    CreatedAtUtc)
                SELECT
                    lower(hex(randomblob(16))),
                    summary.SessionId,
                    NULL,
                    NULL,
                    0,
                    summary.SummaryText,
                    '{"source":"legacy-working-summary"}',
                    summary.UpdatedAtUtc
                FROM AgentWorkingSummaries AS summary
                WHERE trim(summary.SummaryText) <> ''
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentSessionContextCheckpoints AS checkpoint
                      WHERE checkpoint.SessionId = summary.SessionId);

                DROP TABLE AgentWorkingSummaries;
                """;
            workingSummaryCommand.ExecuteNonQuery();
        }

        if (TableExists(connection, transaction, "AgentPermissionRules"))
        {
            using var permissionRuleCommand = connection.CreateCommand();
            permissionRuleCommand.Transaction = transaction;
            permissionRuleCommand.CommandText = "DROP TABLE AgentPermissionRules;";
            permissionRuleCommand.ExecuteNonQuery();
        }
    }

    internal static void ApplySchemaMigrations(SqliteConnection connection, int targetVersion)
    {
        if (targetVersion < 1 || targetVersion > SchemaMigrations[^1].Version)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

        EnableSecureDelete(connection);
        BootstrapAndValidateSchemaMigrationLedger(connection);
        foreach (var migration in SchemaMigrations.Where(item => item.Version <= targetVersion))
        {
            ApplySchemaMigration(connection, migration);
        }
    }

    private static void BootstrapAndValidateSchemaMigrationLedger(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!TableExists(connection, transaction, "SchemaMigrations"))
        {
            using var createCommand = connection.CreateCommand();
            createCommand.Transaction = transaction;
            createCommand.CommandText = """
                CREATE TABLE SchemaMigrations (
                    Version INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Checksum TEXT NOT NULL,
                    AppliedAtUtc TEXT NOT NULL
                );
                """;
            createCommand.ExecuteNonQuery();
            transaction.Commit();
            return;
        }

        var columns = ListTableColumns(connection, transaction, "SchemaMigrations");
        if (!columns.Contains("Version") || !columns.Contains("Name") || !columns.Contains("AppliedAtUtc"))
        {
            throw InvalidLedger("the ledger table does not have the required columns");
        }

        var entries = ReadLedger(connection, transaction, columns.Contains("Checksum"));
        ValidateLedgerEntries(entries, allowMissingChecksum: !columns.Contains("Checksum"));
        if (!columns.Contains("Checksum"))
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.Transaction = transaction;
            alterCommand.CommandText = "ALTER TABLE SchemaMigrations ADD COLUMN Checksum TEXT NULL;";
            alterCommand.ExecuteNonQuery();

            foreach (var entry in entries)
            {
                var migration = SchemaMigrations[entry.Version - 1];
                using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = "UPDATE SchemaMigrations SET Checksum = $checksum WHERE Version = $version;";
                updateCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
                updateCommand.Parameters.AddWithValue("$version", entry.Version);
                updateCommand.ExecuteNonQuery();
            }
        }

        ValidateLedgerEntries(ReadLedger(connection, transaction, hasChecksum: true), allowMissingChecksum: false);
        transaction.Commit();
    }

    private static void ApplySchemaMigration(SqliteConnection connection, SchemaMigration migration)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (HasSchemaMigration(connection, transaction, migration.Version))
        {
            transaction.Commit();
            return;
        }

        migration.Apply(connection, transaction);

        using var ledgerCommand = connection.CreateCommand();
        ledgerCommand.Transaction = transaction;
        ledgerCommand.CommandText = """
            INSERT INTO SchemaMigrations (Version, Name, Checksum, AppliedAtUtc)
            VALUES ($version, $name, $checksum, $appliedAtUtc);
            """;
        ledgerCommand.Parameters.AddWithValue("$version", migration.Version);
        ledgerCommand.Parameters.AddWithValue("$name", migration.Name);
        ledgerCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
        ledgerCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        ledgerCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ValidateLedgerEntries(
        IReadOnlyList<SchemaMigrationLedgerEntry> entries,
        bool allowMissingChecksum)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Version > SchemaMigrations[^1].Version)
            {
                throw InvalidLedger($"migration {entry.Version} is newer than this build supports");
            }

            var expectedVersion = index + 1;
            if (entry.Version != expectedVersion)
            {
                throw InvalidLedger($"expected migration {expectedVersion} but found {entry.Version}");
            }

            var migration = SchemaMigrations[entry.Version - 1];
            if (!string.Equals(entry.Name, migration.Name, StringComparison.Ordinal))
            {
                throw InvalidLedger($"migration {entry.Version} has unknown name '{entry.Name}'");
            }
            if (!allowMissingChecksum
                && !string.Equals(entry.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidLedger($"migration {entry.Version} ('{entry.Name}') has a checksum mismatch");
            }
        }
    }

    private static IReadOnlyList<SchemaMigrationLedgerEntry> ReadLedger(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool hasChecksum)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = hasChecksum
            ? "SELECT Version, Name, Checksum FROM SchemaMigrations ORDER BY Version;"
            : "SELECT Version, Name, NULL FROM SchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var entries = new List<SchemaMigrationLedgerEntry>();
        while (reader.Read())
        {
            entries.Add(new SchemaMigrationLedgerEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return entries;
    }

    private static bool HasSchemaMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM SchemaMigrations WHERE Version = $version LIMIT 1;";
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() is not null;
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> ListTableColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static InvalidOperationException InvalidLedger(string reason)
        => new($"Agent schema migration ledger validation failed: {reason}. "
            + "Back up 'agent/agent.db' and use a Sunder Agent build that recognizes this schema; "
            + "do not edit or delete migration ledger rows.");

    private static SchemaMigration SqlMigration(int version, string name, string sql)
        => new(version, name, sql, (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });

    private sealed record SchemaMigration(
        int Version,
        string Name,
        string ChecksumSource,
        Action<SqliteConnection, SqliteTransaction> Apply)
    {
        public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Version}\n{Name}\n{ChecksumSource}"))).ToLowerInvariant();
    }

    private sealed record SchemaMigrationLedgerEntry(int Version, string Name, string? Checksum);
}

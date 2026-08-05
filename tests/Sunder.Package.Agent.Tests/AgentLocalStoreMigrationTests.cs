using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Storage;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentLocalStoreMigrationTests
{
    [Fact]
    public void ReleasedMigrationIdentities_11Through21MatchGoldenChecksums()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var connection = OpenDatabase(store.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM SchemaMigrations WHERE Version BETWEEN 11 AND 21 ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
        {
            actual.Add($"{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        }

        Assert.Equal(
            """
            11|durable-run-budgets|f528d8f248c95c6aa82d1c346b2267dcf59fa36756cf8c1ac77e5b2c4ef80e18
            12|tool-execution-ledger|1a747509210748ab9bf32eb2e585bb5bd64c065d24063af100717091c48e163e
            13|durable-lifecycle-outbox|05c199fce6fe2f2a5c1efc938a63bbc8b705e32f3c1f1754a795685de7a54e3a
            14|durable-user-turn-admission|c9c1153e0b44aa49b3ec9e72ec846070a8c2cc67250759aa8cdac4b4893aa2c4
            15|anchored-session-context|8c6883abc9214cb8c11f1f5c01efb90f8ae7937e61b3bc0d0bc4204836b5c8d0
            16|tool-execution-provenance|cbcb08d78863e7f74a5bdefeb1c0c02a126ce0c1e7e1b88bee907520b48ad98d
            17|lifecycle-payload-erasure-and-replay-watermarks|f3718ac1e17be97c26cc8d05cfd8bdeb64b4fd72580eca75482731a7b3c415fe
            18|runtime-generation-ownership|23ae0f2ec52c9338a23a16432316971ca64b430af31369b04fbe10478302453c
            19|lifecycle-durability-hardening|d57f7674e7d78bd6942405437a78d485764fbee91411eb7489a2f70dd46a352d
            20|durable-resource-claims|849bc5010a9c700c8cb683e2992e442dc4471fed275135b39f60ae8d54f46fa0
            21|legacy-tool-execution-identities|14347bc760494c9e2b7691278989f71e8f6e37ceb02ab0f5e98c7d583dee29ce
            """.ReplaceLineEndings("\n"),
            string.Join('\n', actual));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void HistoricalSchemaFixture_UpgradesToCurrentSchema(int historicalVersion)
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, historicalVersion);

        var store = new AgentLocalStore(scope.Context);

        using var connection = OpenDatabase(store.DatabasePath);
        Assert.Equal("wal", ExecuteString(connection, "PRAGMA journal_mode;"));
        Assert.Equal(21L, ExecuteInt64(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(21L, ExecuteInt64(connection, "SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.Equal(21L, ExecuteInt64(connection,
            "SELECT COUNT(*) FROM SchemaMigrations WHERE length(Checksum) = 64;"));
        Assert.True(ColumnExists(connection, "AgentPendingPermissionRequests", "ExecutionSnapshotJson"));
        Assert.True(ColumnExists(connection, "AgentTurns", "ContentRevision"));
        Assert.True(ColumnExists(connection, "AgentTurns", "IsStreaming"));
        Assert.True(ColumnExists(connection, "AgentTurns", "RunId"));
        Assert.True(ColumnExists(connection, "AgentTurns", "RunRevision"));
        Assert.True(TableExists(connection, "AgentParentContinuationWork"));
        Assert.True(ColumnExists(connection, "AgentRuns", "ProviderCycleCount"));
        Assert.True(ColumnExists(connection, "AgentRuns", "ToolCallCount"));
        Assert.True(ColumnExists(connection, "AgentRuns", "SubmittedContextTokenCount"));
        Assert.True(TableExists(connection, "AgentToolExecutions"));
        Assert.True(ColumnExists(connection, "AgentToolExecutions", "OwnerPackageId"));
        Assert.True(ColumnExists(connection, "AgentToolExecutions", "ToolSchemaId"));
        Assert.True(ColumnExists(connection, "AgentToolExecutions", "ToolSchemaVersion"));
        Assert.True(ColumnExists(connection, "AgentToolExecutions", "ExecutionTargetOwnerPackageId"));
        Assert.True(ColumnExists(connection, "AgentTurnItems", "ToolExecutionId"));
        Assert.True(ColumnExists(connection, "AgentPendingPermissionRequests", "ToolExecutionId"));
        Assert.True(ColumnExists(connection, "AgentPendingPermissionRequests", "ResourceClaimSetVersion"));
        Assert.True(ColumnExists(connection, "AgentPendingPermissionRequests", "ResourceClaimsJson"));
        Assert.True(IndexExists(connection, "UX_AgentTurnItems_ToolExecutionCall"));
        Assert.True(IndexExists(connection, "UX_AgentTurnItems_ToolExecutionResult"));
        Assert.True(IndexExists(connection, "IX_AgentTurns_RunIdentity"));
        Assert.True(IndexExists(connection, "IX_AgentTurnItems_CallId"));
        Assert.True(IndexExists(connection, "UX_AgentPendingPermissionRequests_ToolExecutionId"));
        Assert.True(TableExists(connection, "AgentLifecycleOutbox"));
        Assert.True(TableExists(connection, "AgentLifecycleSubscriptions"));
        Assert.True(TableExists(connection, "AgentLifecycleDeliveries"));
        Assert.True(ColumnExists(connection, "AgentLifecycleOutbox", "PayloadState"));
        Assert.True(ColumnExists(connection, "AgentLifecycleOutbox", "OriginalPayloadHash"));
        Assert.True(ColumnExists(connection, "AgentLifecycleOutbox", "PayloadErasedAtUtc"));
        Assert.True(ColumnExists(connection, "AgentLifecycleOutbox", "PayloadErasureGeneration"));
        Assert.True(ColumnExists(connection, "AgentLifecycleSubscriptions", "ReplayStartSequence"));
        Assert.True(ColumnExists(connection, "AgentLifecycleSubscriptions", "ReconciledThroughSequence"));
        Assert.True(ColumnExists(connection, "AgentLifecycleSubscriptions", "MembershipGeneration"));
        Assert.True(ColumnExists(connection, "AgentLifecycleSubscriptions", "RetiredAtUtc"));
        Assert.True(ColumnExists(connection, "AgentLifecycleDeliveries", "MembershipGeneration"));
        Assert.True(ColumnExists(connection, "AgentLifecycleDeliveries", "DeliveryVersion"));
        Assert.True(IndexExists(connection, "IX_AgentLifecycleOutbox_Replay"));
        Assert.True(TableExists(connection, "AgentLifecycleGenerationState"));
        Assert.True(TableExists(connection, "AgentSessionDataCleaners"));
        Assert.True(TableExists(connection, "AgentSessionCleanupJobs"));
        Assert.True(TableExists(connection, "AgentLifecycleMaintenanceState"));
        Assert.True(TableExists(connection, "AgentRuntimeGenerations"));
        Assert.True(TableExists(connection, "AgentRuntimeGenerationState"));
        Assert.True(ColumnExists(connection, "AgentRuns", "MemoryConsistencyBarrierEventId"));
        Assert.True(ColumnExists(connection, "AgentRuns", "MemoryConsistencyBarrierPayloadHash"));
        Assert.True(ColumnExists(connection, "AgentRuns", "UserTurnId"));
        Assert.True(ColumnExists(connection, "AgentRuns", "WorkspaceId"));
        Assert.True(ColumnExists(connection, "AgentRuns", "AdmissionKind"));
        Assert.True(ColumnExists(connection, "AgentRuns", "RollbackAnchorTurnId"));
        Assert.True(ColumnExists(connection, "AgentRuns", "RequestFingerprint"));
        Assert.True(ColumnExists(connection, "AgentRuns", "ExecutionStartedAtUtc"));
        Assert.True(IndexExists(connection, "UX_AgentRuns_UserTurnId"));
        Assert.True(IndexExists(connection, "IX_AgentRuns_PreparingDispatch"));
        Assert.True(ColumnExists(connection, "AgentSessions", "TranscriptEpoch"));
        Assert.True(ColumnExists(connection, "AgentSessions", "ActiveContextCheckpointId"));
        Assert.True(ColumnExists(connection, "AgentSessions", "ActiveContextGeneration"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "CheckpointKind"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "TranscriptEpoch"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "CoveredThroughCreatedAtUtc"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "CoveredThroughContentRevision"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "SourceRunId"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "SourceRunRevision"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "SourceRunEpoch"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "Generation"));
        Assert.True(ColumnExists(connection, "AgentSessionContextCheckpoints", "GeneratorVersion"));
        Assert.False(TableExists(connection, "AgentWorkingSummaries"));
        Assert.False(TableExists(connection, "AgentPermissionRules"));
    }

    [Fact]
    public void CurrentSchemaValidation_DoesNotContendWithActiveWriter()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var writer = OpenDatabase(store.DatabasePath);
        Assert.Equal("wal", ExecuteString(writer, "PRAGMA journal_mode;"));
        using var activeWrite = writer.BeginTransaction(deferred: false);
        using var candidate = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        candidate.Open();

        AgentLocalStore.ApplySchemaMigrations(candidate, 21);
    }

    [Fact]
    public void DormantSchemaMigration_PreservesLegacyWorkingSummaryAsContextCheckpoint()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 9);
        var sessionId = Guid.NewGuid();
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE AgentWorkingSummaries (
                    SessionId TEXT PRIMARY KEY,
                    SummaryText TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE AgentPermissionRules (
                    RuleId TEXT PRIMARY KEY,
                    ActionId TEXT NOT NULL,
                    MatcherKind TEXT NOT NULL,
                    Pattern TEXT NOT NULL,
                    Decision TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL
                );
                INSERT INTO AgentWorkingSummaries (SessionId, SummaryText, UpdatedAtUtc)
                VALUES ($sessionId, $summary, $updatedAtUtc);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$summary", "Legacy continuity summary");
            command.Parameters.AddWithValue("$updatedAtUtc", "2026-01-01T00:00:00.0000000+00:00");
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(store.DatabasePath);
        using var select = verification.CreateCommand();
        select.CommandText = "SELECT SummaryText, DetailsJson, CreatedAtUtc, CheckpointKind FROM AgentSessionContextCheckpoints WHERE SessionId = $sessionId;";
        select.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Legacy continuity summary", reader.GetString(0));
        Assert.Equal("{\"source\":\"legacy-working-summary\"}", reader.GetString(1));
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", reader.GetString(2));
        Assert.Equal("Legacy", reader.GetString(3));
        Assert.False(TableExists(verification, "AgentWorkingSummaries"));
        Assert.False(TableExists(verification, "AgentPermissionRules"));
    }

    [Fact]
    public void ToolExecutionProvenanceMigration_LeavesLegacyExecutionsUnowned()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 15);
        var executionId = Guid.NewGuid();
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentToolExecutions (
                    ExecutionId, SessionId, RunId, RunRevision, CallId, ToolId,
                    InvocationFingerprint, IsReadOnly, Status, PreparedAtUtc,
                    StartedAtUtc, FinishedAtUtc, UpdatedAtUtc, OutcomeCode, OutcomeSummary)
                VALUES (
                    $executionId, $sessionId, $runId, 1, 'call-1', 'shell',
                    $fingerprint, 0, 'Completed', $now,
                    $now, $now, $now, 'completed', 'Legacy vendor execution');
                """;
            command.Parameters.AddWithValue("$executionId", executionId.ToString());
            command.Parameters.AddWithValue("$sessionId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$runId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$fingerprint", new string('a', 64));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(store.DatabasePath);
        using var select = verification.CreateCommand();
        select.CommandText = "SELECT OwnerPackageId, ToolSchemaId, ToolSchemaVersion, ExecutionTargetOwnerPackageId FROM AgentToolExecutions WHERE ExecutionId = $executionId;";
        select.Parameters.AddWithValue("$executionId", executionId.ToString());
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    public void LifecycleErasureMigration_AddsWatermarksAndRedactsLegacyFailureMessages()
    {
        const string secretCanary = "legacy-observer-secret-canary";
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 16);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentWorkspaces (
                    WorkspaceId, DisplayName, Description, CreatedAtUtc, UpdatedAtUtc)
                VALUES (
                    'workspace.migration', 'Migration workspace', NULL, $now, $now);

                INSERT INTO AgentLifecycleOutbox (
                    EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                    PayloadJson, PayloadHash, CreatedAtUtc)
                VALUES (
                    'evt_migration_fixture', 'migration-fixture', 'UserTurnAdded',
                    'workspace:migration:root:fixture', 'workspace.migration', NULL,
                    '{}', $payloadHash, $now);

                INSERT INTO AgentLifecycleSubscriptions (
                    SubscriptionId, PackageId, ObserverId, ContractKind, DisplayName,
                    CreatedAtUtc, LastSeenAtUtc)
                VALUES (
                    'sub_migration_fixture', 'test.package', 'migration-observer', 'Durable',
                    'Migration observer', $now, $now);

                INSERT INTO AgentLifecycleDeliveries (
                    SubscriptionId, EventSequence, Status, AttemptCount, NextAttemptAtUtc, LastError)
                VALUES (
                    'sub_migration_fixture', 1, 'Poison', 5, $now, $lastError);
                """;
            command.Parameters.AddWithValue("$payloadHash", new string('a', 64));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$lastError", secretCanary);
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using (var verification = OpenDatabase(store.DatabasePath))
        using (var command = verification.CreateCommand())
        {
            command.CommandText = """
                SELECT outbox.PayloadState,
                       outbox.OriginalPayloadHash,
                       subscription.ReplayStartSequence,
                       subscription.ReconciledThroughSequence,
                       delivery.LastError
                FROM AgentLifecycleOutbox outbox
                INNER JOIN AgentLifecycleDeliveries delivery ON delivery.EventSequence = outbox.Sequence
                INNER JOIN AgentLifecycleSubscriptions subscription
                    ON subscription.SubscriptionId = delivery.SubscriptionId;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Available", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal(1, reader.GetInt64(3));
            Assert.Equal("legacy_observer_failure (redacted)", reader.GetString(4));
        }
        Assert.DoesNotContain(secretCanary, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(databasePath)), StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleHardeningMigration_ErasesDeletedOwnerPayloadAndRearmsReceipt()
    {
        const string secretCanary = "pre-upgrade-deleted-session-secret-canary";
        var deletedSessionId = Guid.NewGuid();
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 18);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentLifecycleOutbox (
                    EventId, SourceKey, EventType, OrderingKey, WorkspaceId, SessionId,
                    PayloadJson, PayloadHash, CreatedAtUtc)
                VALUES (
                    'evt_deleted_owner_fixture', 'deleted-owner-fixture', 'UserTurnAdded',
                    'workspace:deleted:root:fixture', NULL, $sessionId,
                    $payloadJson, $payloadHash, $now);

                INSERT INTO AgentLifecycleSubscriptions (
                    SubscriptionId, PackageId, ObserverId, ContractKind, DisplayName,
                    CreatedAtUtc, LastSeenAtUtc, ReplayStartSequence, ReconciledThroughSequence)
                VALUES (
                    'sub_deleted_owner_fixture', 'test.package', 'deleted-owner-observer',
                    'Durable', 'Deleted owner observer', $now, $now, 1, 1);

                INSERT INTO AgentLifecycleDeliveries (
                    SubscriptionId, EventSequence, Status, AttemptCount, NextAttemptAtUtc,
                    DeliveredAtUtc)
                VALUES (
                    'sub_deleted_owner_fixture', 1, 'Delivered', 0, $now, $now);
                """;
            command.Parameters.AddWithValue("$sessionId", deletedSessionId.ToString());
            command.Parameters.AddWithValue("$payloadJson", $"{{\"secret\":\"{secretCanary}\"}}");
            command.Parameters.AddWithValue("$payloadHash", new string('a', 64));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using (var verification = OpenDatabase(store.DatabasePath))
        using (var command = verification.CreateCommand())
        {
            command.CommandText = """
                SELECT outbox.PayloadJson,
                       outbox.PayloadHash,
                       outbox.PayloadState,
                       outbox.OriginalPayloadHash,
                       outbox.PayloadErasedAtUtc,
                       outbox.PayloadErasureGeneration,
                       delivery.Status,
                       delivery.DeliveryVersion,
                       delivery.MembershipGeneration
                FROM AgentLifecycleOutbox outbox
                INNER JOIN AgentLifecycleDeliveries delivery ON delivery.EventSequence = outbox.Sequence;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("{\"contentErased\":true}", reader.GetString(0));
            Assert.Equal("2cab2434dff48e3639c975cd3ac9693994e3e1ec44a4921628100a047add6950", reader.GetString(1));
            Assert.Equal("Erased", reader.GetString(2));
            Assert.Equal(new string('a', 64), reader.GetString(3));
            Assert.False(reader.IsDBNull(4));
            Assert.Equal(2, reader.GetInt64(5));
            Assert.Equal("Pending", reader.GetString(6));
            Assert.Equal(2, reader.GetInt64(7));
            Assert.Equal(1, reader.GetInt64(8));
        }
        Assert.DoesNotContain(
            secretCanary,
            System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(databasePath)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedMigration_RollsBackSchemaAndLedgerTogether()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 6);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER InterruptMigrationSeven
                BEFORE INSERT ON SchemaMigrations
                WHEN NEW.Version = 7
                BEGIN
                    SELECT RAISE(ABORT, 'simulated migration interruption');
                END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => new AgentLocalStore(scope.Context));

        using var verification = OpenDatabase(databasePath);
        Assert.Equal(6L, ExecuteInt64(verification, "SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.False(ColumnExists(verification, "AgentPendingPermissionRequests", "ExecutionSnapshotJson"));
    }

    [Fact]
    public void VersionEightMigrationFinalizesUnownedStreamingTurns()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 8);
        var turnId = Guid.NewGuid();
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO AgentTurns (TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc, ContentRevision, IsStreaming) VALUES ($turnId, $sessionId, 'Assistant', 'Message', $now, $now, 5, 1);";
            command.Parameters.AddWithValue("$turnId", turnId.ToString());
            command.Parameters.AddWithValue("$sessionId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(store.DatabasePath);
        using var select = verification.CreateCommand();
        select.CommandText = "SELECT ContentRevision, IsStreaming, RunId, RunRevision FROM AgentTurns WHERE TurnId = $turnId;";
        select.Parameters.AddWithValue("$turnId", turnId.ToString());
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(6, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("checksum")]
    [InlineData("newer")]
    [InlineData("unknown")]
    public void InvalidMigrationLedger_FailsClosedWithRecoveryGuidance(string corruption)
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using (var connection = OpenDatabase(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = corruption switch
            {
                "name" => "UPDATE SchemaMigrations SET Name = 'renamed' WHERE Version = 3;",
                "checksum" => "UPDATE SchemaMigrations SET Checksum = 'tampered' WHERE Version = 4;",
                "newer" => "INSERT INTO SchemaMigrations VALUES (22, 'future', 'future', '2026-01-01T00:00:00Z');",
                "unknown" => "UPDATE SchemaMigrations SET Version = 0 WHERE Version = 1;",
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            };
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => new AgentLocalStore(scope.Context));
        Assert.Contains("migration ledger validation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Back up 'agent/agent.db'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("do not edit or delete migration ledger rows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyLedgerWithoutChecksums_IsUpgradedOnlyForKnownMigrations()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 2);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE LegacySchemaMigrations AS
                    SELECT Version, Name, AppliedAtUtc FROM SchemaMigrations;
                DROP TABLE SchemaMigrations;
                ALTER TABLE LegacySchemaMigrations RENAME TO SchemaMigrations;
                """;
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(store.DatabasePath);
        Assert.True(ColumnExists(verification, "SchemaMigrations", "Checksum"));
        Assert.Equal(21L, ExecuteInt64(verification,
            "SELECT COUNT(*) FROM SchemaMigrations WHERE length(Checksum) = 64;"));
    }

    [Theory]
    [InlineData("local-resource-v3:legacy")]
    [InlineData("docker-resource-v3:legacy")]
    public void DurableResourceClaimMigration_MarksLegacyTransientAuthorityAsUnrestorable(
        string resourceReference)
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 19);
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentPendingPermissionRequests (
                    RequestId, SessionId, ActionId, Summary, ResourceReference, CreatedAtUtc)
                VALUES (
                    'legacy-authority', $sessionId, 'files.read', 'Legacy authority',
                    $resourceReference, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$sessionId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$resourceReference", resourceReference);
            command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        _ = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(databasePath);
        Assert.Equal(-1L, ExecuteInt64(
            verification,
            "SELECT ResourceClaimSetVersion FROM AgentPendingPermissionRequests WHERE RequestId = 'legacy-authority';"));
    }

    [Fact]
    public void LegacyToolExecutionIdentityMigration_PairsWithinRunAndPreventsReusedCallCrossPairing()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 20);
        var sessionId = Guid.NewGuid();
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var firstCallItemId = Guid.NewGuid();
        var firstResultItemId = Guid.NewGuid();
        var secondCallItemId = Guid.NewGuid();
        var secondResultItemId = Guid.NewGuid();
        using (var connection = OpenDatabase(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO AgentTurns (
                    TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc,
                    ContentRevision, IsStreaming, RunId, RunRevision)
                VALUES
                    ('00000000-0000-0000-0000-000000000101', $sessionId, 'Assistant', 'ToolCall',
                     '2026-01-01T00:00:01.0000000+00:00', '2026-01-01T00:00:01.0000000+00:00', 1, 0, $firstRunId, 1),
                    ('00000000-0000-0000-0000-000000000102', $sessionId, 'Tool', 'ToolResult',
                     '2026-01-01T00:00:02.0000000+00:00', '2026-01-01T00:00:02.0000000+00:00', 1, 0, $firstRunId, 1),
                    ('00000000-0000-0000-0000-000000000201', $sessionId, 'Assistant', 'ToolCall',
                     '2026-01-01T00:00:03.0000000+00:00', '2026-01-01T00:00:03.0000000+00:00', 1, 0, $secondRunId, 1),
                    ('00000000-0000-0000-0000-000000000202', $sessionId, 'Tool', 'ToolResult',
                     '2026-01-01T00:00:04.0000000+00:00', '2026-01-01T00:00:04.0000000+00:00', 1, 0, $secondRunId, 1);

                INSERT INTO AgentTurnItems (
                    ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId,
                    ArgumentsJson, ResultSummary, WasTruncated, IsError, ToolExecutionId)
                VALUES
                    ($firstCallItemId, '00000000-0000-0000-0000-000000000101', 0, 'ToolCall',
                     NULL, 'reused-call', 'legacy_tool', '{"run":1}', NULL, 0, 0, NULL),
                    ($firstResultItemId, '00000000-0000-0000-0000-000000000102', 0, 'ToolResult',
                     'first-output', 'reused-call', 'legacy_tool', NULL, 'first-summary', 0, 0, NULL),
                    ($secondCallItemId, '00000000-0000-0000-0000-000000000201', 0, 'ToolCall',
                     NULL, 'reused-call', 'legacy_tool', '{"run":2}', NULL, 0, 0, NULL),
                    ($secondResultItemId, '00000000-0000-0000-0000-000000000202', 0, 'ToolResult',
                     'second-output', 'reused-call', 'legacy_tool', NULL, 'second-summary', 0, 0, NULL);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            command.Parameters.AddWithValue("$firstRunId", firstRunId.ToString());
            command.Parameters.AddWithValue("$secondRunId", secondRunId.ToString());
            command.Parameters.AddWithValue("$firstCallItemId", firstCallItemId.ToString());
            command.Parameters.AddWithValue("$firstResultItemId", firstResultItemId.ToString());
            command.Parameters.AddWithValue("$secondCallItemId", secondCallItemId.ToString());
            command.Parameters.AddWithValue("$secondResultItemId", secondResultItemId.ToString());
            command.ExecuteNonQuery();
        }

        var store = new AgentLocalStore(scope.Context);

        using (var verification = OpenDatabase(databasePath))
        using (var command = verification.CreateCommand())
        {
            command.CommandText = """
                SELECT t.RunId, i.ToolExecutionId
                FROM AgentTurnItems i
                INNER JOIN AgentTurns t ON t.TurnId = i.TurnId
                ORDER BY t.RunId, i.Kind;
                """;
            using var reader = command.ExecuteReader();
            var identities = new Dictionary<Guid, HashSet<Guid>>();
            while (reader.Read())
            {
                var runId = Guid.Parse(reader.GetString(0));
                if (!identities.TryGetValue(runId, out var executionIds))
                {
                    identities[runId] = executionIds = [];
                }
                executionIds.Add(Guid.Parse(reader.GetString(1)));
            }
            Assert.Single(identities[firstRunId]);
            Assert.Single(identities[secondRunId]);
            Assert.NotEqual(identities[firstRunId].Single(), identities[secondRunId].Single());
        }

        var first = store.GetTranscriptToolDetail(new(
            sessionId,
            CallId: "reused-call",
            RunId: firstRunId,
            RunRevision: 1));
        var second = store.GetTranscriptToolDetail(new(
            sessionId,
            CallId: "reused-call",
            RunId: secondRunId,
            RunRevision: 1));

        Assert.Equal("first-output", first?.OutputText);
        Assert.Equal("{\"run\":1}", first?.ArgumentsJson);
        Assert.Equal(firstRunId, first?.RunId);
        Assert.Equal("second-output", second?.OutputText);
        Assert.Equal("{\"run\":2}", second?.ArgumentsJson);
        Assert.Equal(secondRunId, second?.RunId);
        Assert.Null(store.GetTranscriptToolDetail(new(
            sessionId,
            CallId: "reused-call")));
    }

    [Fact]
    public void LegacyToolExecutionIdentityMigration_BackfillsRunlessPairsBySessionCallAndOrdinal()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 20);
        var firstSessionId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var secondSessionId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
        var firstCallItemId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var firstResultItemId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var secondCallItemId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var secondResultItemId = Guid.Parse("00000000-0000-0000-0000-000000000202");
        var otherCallItemId = Guid.Parse("00000000-0000-0000-0000-000000000301");
        var otherResultItemId = Guid.Parse("00000000-0000-0000-0000-000000000302");
        using (var connection = OpenDatabase(databasePath))
        {
            InsertLegacyRunlessToolPair(
                connection,
                firstSessionId,
                "00000000-0000-0000-0000-000000001001",
                "00000000-0000-0000-0000-000000001002",
                firstCallItemId,
                firstResultItemId,
                "2026-01-01T00:00:01.0000000+00:00",
                "2026-01-01T00:00:02.0000000+00:00",
                "first-output");
            InsertLegacyRunlessToolPair(
                connection,
                firstSessionId,
                "00000000-0000-0000-0000-000000002001",
                "00000000-0000-0000-0000-000000002002",
                secondCallItemId,
                secondResultItemId,
                "2026-01-01T00:00:03.0000000+00:00",
                "2026-01-01T00:00:04.0000000+00:00",
                "second-output");
            InsertLegacyRunlessToolPair(
                connection,
                secondSessionId,
                "00000000-0000-0000-0000-000000003001",
                "00000000-0000-0000-0000-000000003002",
                otherCallItemId,
                otherResultItemId,
                "2026-01-01T00:00:05.0000000+00:00",
                "2026-01-01T00:00:06.0000000+00:00",
                "other-output");
        }

        var store = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(databasePath);
        using var command = verification.CreateCommand();
        command.CommandText = "SELECT ItemId, ToolExecutionId FROM AgentTurnItems WHERE CallId = 'reused-call';";
        using var reader = command.ExecuteReader();
        var executionIds = new Dictionary<Guid, Guid>();
        while (reader.Read())
        {
            executionIds[Guid.Parse(reader.GetString(0))] = Guid.Parse(reader.GetString(1));
        }

        var firstExecutionId = executionIds[firstCallItemId];
        var secondExecutionId = executionIds[secondCallItemId];
        var otherExecutionId = executionIds[otherCallItemId];
        Assert.Equal(firstExecutionId, executionIds[firstResultItemId]);
        Assert.Equal(secondExecutionId, executionIds[secondResultItemId]);
        Assert.Equal(otherExecutionId, executionIds[otherResultItemId]);
        Assert.Equal(CreateExpectedRunlessExecutionId(firstSessionId, "reused-call", 0), firstExecutionId);
        Assert.Equal(CreateExpectedRunlessExecutionId(firstSessionId, "reused-call", 1), secondExecutionId);
        Assert.Equal(CreateExpectedRunlessExecutionId(secondSessionId, "reused-call", 0), otherExecutionId);
        Assert.NotEqual(firstExecutionId, secondExecutionId);
        Assert.NotEqual(firstExecutionId, otherExecutionId);

        var first = store.GetTranscriptToolDetail(new(firstSessionId, ItemId: firstCallItemId));
        var second = store.GetTranscriptToolDetail(new(firstSessionId, ItemId: secondCallItemId));
        Assert.Equal("first-output", first?.OutputText);
        Assert.Equal("second-output", second?.OutputText);
        Assert.Null(first?.RunId);
        Assert.Null(second?.RunRevision);
    }

    [Fact]
    public void LegacyToolExecutionIdentityMigration_PairsNearestCompatiblePrecedingCallsAndLeavesOrphans()
    {
        using var scope = RegressionTestPackageScope.Create();
        var databasePath = GetDatabasePath(scope);
        CreateHistoricalFixture(databasePath, 20);
        var sessionId = Guid.NewGuid();
        var orphanCallA = Guid.NewGuid();
        var callB = Guid.NewGuid();
        var resultB = Guid.NewGuid();
        var fartherCall = Guid.NewGuid();
        var nearerCall = Guid.NewGuid();
        var nearestResult = Guid.NewGuid();
        var precedingResult = Guid.NewGuid();
        var followingCall = Guid.NewGuid();
        var toolCallA = Guid.NewGuid();
        var toolCallB = Guid.NewGuid();
        var toolResultA = Guid.NewGuid();
        var toolResultB = Guid.NewGuid();
        using (var connection = OpenDatabase(databasePath))
        {
            InsertLegacyRunlessToolItem(connection, sessionId, orphanCallA, "2026-01-01T00:00:01.0000000+00:00", "ToolCall", "orphan-then-pair", "tool-a");
            InsertLegacyRunlessToolItem(connection, sessionId, callB, "2026-01-01T00:00:02.0000000+00:00", "ToolCall", "orphan-then-pair", "tool-b");
            InsertLegacyRunlessToolItem(connection, sessionId, resultB, "2026-01-01T00:00:03.0000000+00:00", "ToolResult", "orphan-then-pair", "tool-b");

            InsertLegacyRunlessToolItem(connection, sessionId, fartherCall, "2026-01-01T00:00:04.0000000+00:00", "ToolCall", "nearest", "same-tool");
            InsertLegacyRunlessToolItem(connection, sessionId, nearerCall, "2026-01-01T00:00:05.0000000+00:00", "ToolCall", "nearest", "same-tool");
            InsertLegacyRunlessToolItem(connection, sessionId, nearestResult, "2026-01-01T00:00:06.0000000+00:00", "ToolResult", "nearest", "same-tool");

            InsertLegacyRunlessToolItem(connection, sessionId, precedingResult, "2026-01-01T00:00:07.0000000+00:00", "ToolResult", "result-before-call", "same-tool");
            InsertLegacyRunlessToolItem(connection, sessionId, followingCall, "2026-01-01T00:00:08.0000000+00:00", "ToolCall", "result-before-call", "same-tool");

            InsertLegacyRunlessToolItem(connection, sessionId, toolCallA, "2026-01-01T00:00:09.0000000+00:00", "ToolCall", "tool-order", "tool-a");
            InsertLegacyRunlessToolItem(connection, sessionId, toolCallB, "2026-01-01T00:00:10.0000000+00:00", "ToolCall", "tool-order", "tool-b");
            InsertLegacyRunlessToolItem(connection, sessionId, toolResultA, "2026-01-01T00:00:11.0000000+00:00", "ToolResult", "tool-order", "tool-a");
            InsertLegacyRunlessToolItem(connection, sessionId, toolResultB, "2026-01-01T00:00:12.0000000+00:00", "ToolResult", "tool-order", "tool-b");
        }

        _ = new AgentLocalStore(scope.Context);

        using var verification = OpenDatabase(databasePath);
        using var command = verification.CreateCommand();
        command.CommandText = "SELECT ItemId, ToolExecutionId FROM AgentTurnItems WHERE TurnId IN (SELECT TurnId FROM AgentTurns WHERE SessionId = $sessionId);";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        var executionIds = new Dictionary<Guid, Guid>();
        while (reader.Read())
        {
            executionIds[Guid.Parse(reader.GetString(0))] = Guid.Parse(reader.GetString(1));
        }

        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "orphan-then-pair", 0), executionIds[orphanCallA]);
        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "orphan-then-pair", 1), executionIds[callB]);
        Assert.Equal(executionIds[callB], executionIds[resultB]);
        Assert.NotEqual(executionIds[orphanCallA], executionIds[resultB]);

        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "nearest", 0), executionIds[fartherCall]);
        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "nearest", 1), executionIds[nearerCall]);
        Assert.Equal(executionIds[nearerCall], executionIds[nearestResult]);
        Assert.NotEqual(executionIds[fartherCall], executionIds[nearestResult]);

        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "result-before-call", 0), executionIds[precedingResult]);
        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "result-before-call", 1), executionIds[followingCall]);
        Assert.NotEqual(executionIds[precedingResult], executionIds[followingCall]);

        Assert.Equal(executionIds[toolCallA], executionIds[toolResultA]);
        Assert.Equal(executionIds[toolCallB], executionIds[toolResultB]);
        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "tool-order", 0), executionIds[toolCallA]);
        Assert.Equal(CreateExpectedRunlessExecutionId(sessionId, "tool-order", 1), executionIds[toolCallB]);
    }

    private static void CreateHistoricalFixture(string databasePath, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = OpenDatabase(databasePath);
        AgentLocalStore.ApplySchemaMigrations(connection, version);
    }

    private static void InsertLegacyRunlessToolPair(
        SqliteConnection connection,
        Guid sessionId,
        string callTurnId,
        string resultTurnId,
        Guid callItemId,
        Guid resultItemId,
        string callTimestamp,
        string resultTimestamp,
        string output)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentTurns (
                TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc,
                ContentRevision, IsStreaming, RunId, RunRevision)
            VALUES
                ($callTurnId, $sessionId, 'Assistant', 'ToolCall', $callTimestamp, $callTimestamp,
                 1, 0, NULL, NULL),
                ($resultTurnId, $sessionId, 'Tool', 'ToolResult', $resultTimestamp, $resultTimestamp,
                 1, 0, NULL, NULL);

            INSERT INTO AgentTurnItems (
                ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId,
                ArgumentsJson, ResultSummary, WasTruncated, IsError, ToolExecutionId)
            VALUES
                ($callItemId, $callTurnId, 0, 'ToolCall', NULL, 'reused-call', 'legacy_tool',
                 '{}', NULL, 0, 0, NULL),
                ($resultItemId, $resultTurnId, 0, 'ToolResult', $output, 'reused-call', 'legacy_tool',
                 NULL, NULL, 0, 0, NULL);
            """;
        command.Parameters.AddWithValue("$callTurnId", callTurnId);
        command.Parameters.AddWithValue("$resultTurnId", resultTurnId);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$callTimestamp", callTimestamp);
        command.Parameters.AddWithValue("$resultTimestamp", resultTimestamp);
        command.Parameters.AddWithValue("$callItemId", callItemId.ToString());
        command.Parameters.AddWithValue("$resultItemId", resultItemId.ToString());
        command.Parameters.AddWithValue("$output", output);
        command.ExecuteNonQuery();
    }

    private static void InsertLegacyRunlessToolItem(
        SqliteConnection connection,
        Guid sessionId,
        Guid itemId,
        string timestamp,
        string kind,
        string callId,
        string? toolId)
    {
        var turnId = Guid.NewGuid();
        var isResult = string.Equals(kind, "ToolResult", StringComparison.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentTurns (
                TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc,
                ContentRevision, IsStreaming, RunId, RunRevision)
            VALUES ($turnId, $sessionId, $role, $kind, $timestamp, $timestamp,
                    1, 0, NULL, NULL);

            INSERT INTO AgentTurnItems (
                ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId,
                ArgumentsJson, ResultSummary, WasTruncated, IsError, ToolExecutionId)
            VALUES ($itemId, $turnId, 0, $kind, $textContent, $callId, $toolId,
                    $argumentsJson, NULL, 0, 0, NULL);
            """;
        command.Parameters.AddWithValue("$turnId", turnId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$role", isResult ? "Tool" : "Assistant");
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$itemId", itemId.ToString());
        command.Parameters.AddWithValue("$textContent", isResult ? "result" : DBNull.Value);
        command.Parameters.AddWithValue("$callId", callId);
        command.Parameters.AddWithValue("$toolId", (object?)toolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", isResult ? DBNull.Value : "{\"value\":true}");
        command.ExecuteNonQuery();
    }

    private static Guid CreateExpectedRunlessExecutionId(
        Guid sessionId,
        string callId,
        int pairIndex)
    {
        var material = string.Join(
            '\n',
            "sunder-tool-execution-v21-runless",
            sessionId.ToString("D"),
            pairIndex.ToString(CultureInfo.InvariantCulture),
            callId.Length.ToString(CultureInfo.InvariantCulture),
            callId);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
    }

    private static string GetDatabasePath(RegressionTestPackageScope scope)
        => scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ExecuteString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IndexExists(SqliteConnection connection, string indexName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", indexName);
        return command.ExecuteScalar() is not null;
    }
}

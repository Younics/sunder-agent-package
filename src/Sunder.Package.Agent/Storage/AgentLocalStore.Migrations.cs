using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private const int LegacySchemaBaselineVersion = 1;

    private static readonly SchemaMigration[] SchemaMigrations =
    [
        new(
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
        new(
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
        new(
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
        new(
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
        new(
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
    ];

    private void ApplySchemaMigrations()
    {
        using var connection = CreateConnection();
        connection.Open();

        BootstrapSchemaMigrationLedger(connection);
        foreach (var migration in SchemaMigrations)
        {
            ApplySchemaMigration(connection, migration);
        }
    }

    private static void BootstrapSchemaMigrationLedger(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );

            INSERT OR IGNORE INTO SchemaMigrations (Version, Name, AppliedAtUtc)
            VALUES ($version, $name, $appliedAtUtc);
            """;
        command.Parameters.AddWithValue("$version", LegacySchemaBaselineVersion);
        command.Parameters.AddWithValue("$name", "legacy-schema-baseline");
        command.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
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

        using (var migrationCommand = connection.CreateCommand())
        {
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = migration.Sql;
            migrationCommand.ExecuteNonQuery();
        }

        using (var ledgerCommand = connection.CreateCommand())
        {
            ledgerCommand.Transaction = transaction;
            ledgerCommand.CommandText = """
                INSERT INTO SchemaMigrations (Version, Name, AppliedAtUtc)
                VALUES ($version, $name, $appliedAtUtc);
                """;
            ledgerCommand.Parameters.AddWithValue("$version", migration.Version);
            ledgerCommand.Parameters.AddWithValue("$name", migration.Name);
            ledgerCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            ledgerCommand.ExecuteNonQuery();
        }

        transaction.Commit();
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

    private sealed record SchemaMigration(int Version, string Name, string Sql);
}

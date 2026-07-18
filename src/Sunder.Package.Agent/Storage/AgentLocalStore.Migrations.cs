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
    ];

    private void ApplySchemaMigrations()
    {
        using var connection = CreateConnection();
        connection.Open();
        ApplySchemaMigrations(connection, SchemaMigrations[^1].Version);
    }

    internal static void ApplySchemaMigrations(SqliteConnection connection, int targetVersion)
    {
        if (targetVersion < 1 || targetVersion > SchemaMigrations[^1].Version)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

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

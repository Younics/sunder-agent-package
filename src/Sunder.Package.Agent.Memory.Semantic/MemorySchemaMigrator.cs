using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class MemorySchemaMigrator(string databasePath)
{
    internal const int CurrentVersion = 11;

    private const int MigrationIntegrityVersion = 10;
    private const string LegacySecureEraseMaintenance = "legacy-secure-erase-v10";
    private const string FtsSecureEraseMaintenance = "fts5-secure-erase-v11";
    private static readonly Version MinimumFtsSecureDeleteVersion = new(3, 42, 0);
    private static readonly SchemaMigration[] Migrations =
    [
        new(1, "Create memory, evidence, and embedding tables", "CreateInitialSchema-v1", CreateInitialSchema),
        new(2, "Add memory supersession lineage", "AddSupersessionColumn-v1", AddSupersessionColumn),
        new(3, "Create and populate full-text search", "CreateSearchSchema-v1", CreateSearchSchema),
        new(4, "Add staged embedding generations", "AddEmbeddingGenerations-v1", AddEmbeddingGenerations),
        new(5, "Add memory provenance", "AddMemoryProvenance-v1", AddMemoryProvenance),
        new(6, "Add durable lifecycle inbox and retraction lineage", "AddDurableLifecycleInbox-v1", AddDurableLifecycleInbox),
        new(7, "Track embedding configuration fingerprints", "AddEmbeddingConfigurationFingerprint-v1", AddEmbeddingConfigurationFingerprint),
        new(8, "Canonicalize embedding provider identities", "CanonicalizeEmbeddingProviderIdentities-v1", CanonicalizeEmbeddingProviderIdentities),
        new(9, "Add resumable embedding generation source fences", "AddEmbeddingGenerationSourceFences-v1", AddEmbeddingGenerationSourceFences),
        new(10, "Harden migration ledger and secure deleted content", "AddMigrationIntegrityAndSecureErase-v1", AddMigrationIntegrityAndSecureErase),
        new(11, "Enable secure deletion for full-text memory index", "EnableFts5SecureDelete-v1", EnableFts5SecureDelete),
    ];

    private readonly string _databasePath = databasePath;

    public void Migrate()
    {
        using (var connection = MemoryDatabase.OpenConnection(_databasePath))
        {
            EnableWal(connection);
            using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
            EnsureLedger(connection, transaction);
            var hasChecksum = HasColumn(connection, transaction, "MemorySchemaMigrations", "Checksum");
            var appliedMigrations = ReadLedger(connection, transaction, hasChecksum);
            ValidateLedger(appliedMigrations, hasChecksum);
            foreach (var migration in Migrations)
            {
                ApplyMigration(connection, transaction, appliedMigrations, migration);
            }

            ValidateLedger(ReadLedger(connection, transaction, hasChecksum: true), hasChecksum: true);
            if (!HasTable(connection, transaction, "SessionMemorySearch"))
            {
                CreateSearchSchema(connection, transaction);
            }
            EnsureFts5SecureDeleteConfigured(connection, transaction);
            if (SearchIndexNeedsRepair(connection, transaction))
            {
                RepairSearchIndex(connection, transaction);
            }
            transaction.Commit();
        }

        EnsureSecureEraseMaintenance();
    }

    private static void EnableWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }

    private static void EnsureLedger(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MemorySchemaMigrations (
                Version INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static List<MigrationLedgerEntry> ReadLedger(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool hasChecksum)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = hasChecksum
            ? "SELECT Version, Name, Checksum FROM MemorySchemaMigrations ORDER BY Version;"
            : "SELECT Version, Name, NULL FROM MemorySchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var entries = new List<MigrationLedgerEntry>();
        while (reader.Read())
        {
            entries.Add(new MigrationLedgerEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return entries;
    }

    private static void ValidateLedger(IReadOnlyList<MigrationLedgerEntry> entries, bool hasChecksum)
    {
        if (!hasChecksum && entries.Any(entry => entry.Version >= MigrationIntegrityVersion))
        {
            throw InvalidLedger("migration 10 is missing its checksum column");
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var expectedVersion = index + 1;
            if (entry.Version < 1 || entry.Version > CurrentVersion)
            {
                throw InvalidLedger($"migration {entry.Version} is newer than this build supports");
            }
            if (entry.Version != expectedVersion)
            {
                throw InvalidLedger($"expected migration {expectedVersion} but found {entry.Version}");
            }

            var migration = Migrations[entry.Version - 1];
            if (!string.Equals(entry.Name, migration.Name, StringComparison.Ordinal))
            {
                throw InvalidLedger($"migration {entry.Version} has unknown name '{entry.Name}'");
            }
            if (hasChecksum
                && !string.Equals(entry.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidLedger($"migration {entry.Version} ('{entry.Name}') has a checksum mismatch");
            }
        }
    }

    private static void ApplyMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ICollection<MigrationLedgerEntry> appliedMigrations,
        SchemaMigration migration)
    {
        if (appliedMigrations.Any(entry => entry.Version == migration.Version))
        {
            return;
        }

        migration.Apply(connection, transaction);
        var hasChecksum = HasColumn(connection, transaction, "MemorySchemaMigrations", "Checksum");
        using var ledgerCommand = connection.CreateCommand();
        ledgerCommand.Transaction = transaction;
        ledgerCommand.CommandText = hasChecksum
            ? """
                INSERT INTO MemorySchemaMigrations (Version, Name, Checksum, AppliedAtUtc)
                VALUES ($version, $name, $checksum, $appliedAtUtc);
                """
            : """
                INSERT INTO MemorySchemaMigrations (Version, Name, AppliedAtUtc)
                VALUES ($version, $name, $appliedAtUtc);
                """;
        ledgerCommand.Parameters.AddWithValue("$version", migration.Version);
        ledgerCommand.Parameters.AddWithValue("$name", migration.Name);
        if (hasChecksum)
        {
            ledgerCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
        }
        ledgerCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        if (ledgerCommand.ExecuteNonQuery() != 1)
        {
            throw InvalidLedger($"migration {migration.Version} could not be recorded");
        }
        appliedMigrations.Add(new MigrationLedgerEntry(
            migration.Version,
            migration.Name,
            hasChecksum ? migration.Checksum : null));
    }

    private static void CreateInitialSchema(SqliteConnection connection, SqliteTransaction transaction)
        => Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SessionMemories (
                MemoryId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                Category TEXT NOT NULL,
                Content TEXT NOT NULL,
                NormalizedContent TEXT NOT NULL,
                EvidenceText TEXT NULL,
                SourceTurnId TEXT NULL,
                Importance REAL NOT NULL,
                Confidence REAL NOT NULL,
                IsPinned INTEGER NOT NULL DEFAULT 0,
                State TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastAccessedAtUtc TEXT NULL,
                AccessCount INTEGER NOT NULL DEFAULT 0,
                Provenance TEXT NOT NULL DEFAULT 'Unknown'
            );
            CREATE TABLE IF NOT EXISTS SessionMemoryEvidence (
                EvidenceId TEXT PRIMARY KEY,
                MemoryId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                SourceTurnId TEXT NULL,
                EvidenceText TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SessionMemoryEmbeddings (
                MemoryId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                CanonicalTextHash TEXT NOT NULL,
                Dimensions INTEGER NOT NULL,
                VectorJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionMemories_SessionId_Category_NormalizedContent
                ON SessionMemories (SessionId, Category, NormalizedContent);
            CREATE INDEX IF NOT EXISTS IX_SessionMemories_SessionId_State_UpdatedAtUtc
                ON SessionMemories (SessionId, State, UpdatedAtUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryEvidence_MemoryId_CreatedAtUtc
                ON SessionMemoryEvidence (MemoryId, CreatedAtUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryEmbeddings_SessionId_ProviderId_ModelId
                ON SessionMemoryEmbeddings (SessionId, ProviderId, ModelId);
            """);

    private static void AddSupersessionColumn(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "SessionMemories", "SupersededByMemoryId"))
        {
            Execute(connection, transaction, "ALTER TABLE SessionMemories ADD COLUMN SupersededByMemoryId TEXT NULL;");
        }
    }

    private static void CreateSearchSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE VIRTUAL TABLE IF NOT EXISTS SessionMemorySearch USING fts5(
                MemoryId UNINDEXED,
                SessionId UNINDEXED,
                Category,
                Content,
                EvidenceText,
                State UNINDEXED
            );
            """);
        RepairSearchIndex(connection, transaction);
    }

    private static void AddEmbeddingGenerations(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SessionMemoryEmbeddingGenerations (
                GenerationId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                State TEXT NOT NULL,
                ExpectedMemoryCount INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS SessionMemoryActiveEmbeddingGenerations (
                SessionId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                GenerationId TEXT NOT NULL,
                PRIMARY KEY (SessionId, ProviderId, ModelId)
            );
            """);

        if (!HasColumn(connection, transaction, "SessionMemoryEmbeddings", "GenerationId"))
        {
            Execute(connection, transaction, "ALTER TABLE SessionMemoryEmbeddings RENAME TO SessionMemoryEmbeddingsLegacy;");
            CreateGenerationEmbeddingTable(connection, transaction);
            MigrateLegacyEmbeddings(connection, transaction);
            Execute(connection, transaction, "DROP TABLE SessionMemoryEmbeddingsLegacy;");
        }
        else
        {
            CreateGenerationEmbeddingTable(connection, transaction);
        }

        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryEmbeddings_SessionId_ProviderId_ModelId
                ON SessionMemoryEmbeddings (SessionId, ProviderId, ModelId);
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryEmbeddingGenerations_SessionProviderModel
                ON SessionMemoryEmbeddingGenerations (SessionId, ProviderId, ModelId, State);
            """);
    }

    private static void AddMemoryProvenance(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "SessionMemories", "Provenance"))
        {
            Execute(connection, transaction, "ALTER TABLE SessionMemories ADD COLUMN Provenance TEXT NOT NULL DEFAULT 'Unknown';");
        }
    }

    private static void AddDurableLifecycleInbox(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "SessionMemories", "MemoryRevision"))
        {
            Execute(connection, transaction, "ALTER TABLE SessionMemories ADD COLUMN MemoryRevision INTEGER NOT NULL DEFAULT 1;");
        }
        if (!HasColumn(connection, transaction, "SessionMemories", "IsManual"))
        {
            // Existing unlineaged memories are preserved conservatively during future retractions.
            Execute(connection, transaction, "ALTER TABLE SessionMemories ADD COLUMN IsManual INTEGER NOT NULL DEFAULT 1;");
        }
        if (!HasColumn(connection, transaction, "SessionMemoryEvidence", "ContributionId"))
        {
            Execute(connection, transaction, "ALTER TABLE SessionMemoryEvidence ADD COLUMN ContributionId TEXT NULL;");
        }
        if (!HasColumn(connection, transaction, "SessionMemoryEmbeddings", "MemoryRevision"))
        {
            Execute(connection, transaction, "ALTER TABLE SessionMemoryEmbeddings ADD COLUMN MemoryRevision INTEGER NOT NULL DEFAULT 0;");
        }

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SemanticLifecycleInbox (
                EventId TEXT PRIMARY KEY,
                EventType TEXT NOT NULL,
                PayloadHash TEXT NOT NULL CHECK (length(PayloadHash) = 64),
                SourceKey TEXT NOT NULL,
                EventSequence INTEGER NOT NULL,
                ReceivedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SessionMemoryContributions (
                ContributionId TEXT PRIMARY KEY,
                EventId TEXT NOT NULL,
                MemoryId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                ProfileId TEXT NOT NULL,
                SourceTurnId TEXT NOT NULL,
                Category TEXT NOT NULL,
                Content TEXT NOT NULL,
                NormalizedContent TEXT NOT NULL,
                EvidenceText TEXT NULL,
                Importance REAL NOT NULL,
                Confidence REAL NOT NULL,
                IsPinned INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UNIQUE (EventId, ContributionId)
            );

            CREATE TABLE IF NOT EXISTS SessionMemoryTurnRetractions (
                SessionId TEXT NOT NULL,
                TurnId TEXT NOT NULL,
                EventId TEXT NOT NULL,
                PayloadHash TEXT NOT NULL,
                RetractedAtUtc TEXT NOT NULL,
                PRIMARY KEY (SessionId, TurnId)
            );

            CREATE TABLE IF NOT EXISTS SessionMemoryDeletionTombstones (
                SessionId TEXT PRIMARY KEY,
                WorkspaceId TEXT NULL,
                EventId TEXT NOT NULL,
                PayloadHash TEXT NOT NULL,
                DeletedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS WorkspaceMemoryDeletionTombstones (
                WorkspaceId TEXT PRIMARY KEY,
                EventId TEXT NOT NULL,
                PayloadHash TEXT NOT NULL,
                DeletedAtUtc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionMemoryEvidence_ContributionId
                ON SessionMemoryEvidence (ContributionId)
                WHERE ContributionId IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryContributions_SourceTurn
                ON SessionMemoryContributions (SessionId, SourceTurnId);
            CREATE INDEX IF NOT EXISTS IX_SessionMemoryContributions_Memory
                ON SessionMemoryContributions (MemoryId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_SemanticLifecycleInbox_Sequence
                ON SemanticLifecycleInbox (EventSequence);
            """);
    }

    private static void AddEmbeddingConfigurationFingerprint(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "SessionMemoryEmbeddingGenerations", "ConfigurationFingerprint"))
        {
            Execute(
                connection,
                transaction,
                "ALTER TABLE SessionMemoryEmbeddingGenerations ADD COLUMN ConfigurationFingerprint TEXT NULL;");
        }
    }

    private static void CanonicalizeEmbeddingProviderIdentities(
        SqliteConnection connection,
        SqliteTransaction transaction)
        => Execute(connection, transaction, """
            DROP TABLE IF EXISTS TempCanonicalActiveEmbeddingGenerations;
            CREATE TEMP TABLE TempCanonicalActiveEmbeddingGenerations (
                SessionId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                GenerationId TEXT NOT NULL,
                PRIMARY KEY (SessionId, ProviderId, ModelId)
            );

            INSERT INTO TempCanonicalActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
            SELECT SessionId, ProviderId, ModelId, GenerationId
            FROM (
                SELECT active.SessionId,
                       lower(trim(active.ProviderId)) AS ProviderId,
                       active.ModelId,
                       active.GenerationId,
                       ROW_NUMBER() OVER (
                           PARTITION BY active.SessionId, lower(trim(active.ProviderId)), active.ModelId
                           ORDER BY generation.CompletedAtUtc DESC,
                                    generation.CreatedAtUtc DESC,
                                    active.GenerationId DESC) AS CanonicalRank
                FROM SessionMemoryActiveEmbeddingGenerations active
                INNER JOIN SessionMemoryEmbeddingGenerations generation
                    ON generation.GenerationId = active.GenerationId
            )
            WHERE CanonicalRank = 1;

            DELETE FROM SessionMemoryActiveEmbeddingGenerations;
            UPDATE SessionMemoryEmbeddingGenerations SET ProviderId = lower(trim(ProviderId));
            UPDATE SessionMemoryEmbeddings SET ProviderId = lower(trim(ProviderId));
            INSERT INTO SessionMemoryActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
            SELECT SessionId, ProviderId, ModelId, GenerationId
            FROM TempCanonicalActiveEmbeddingGenerations;
            DROP TABLE TempCanonicalActiveEmbeddingGenerations;
            """);

    private static void AddEmbeddingGenerationSourceFences(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "SessionMemoryEmbeddingGenerations", "SourceFingerprint"))
        {
            Execute(
                connection,
                transaction,
                "ALTER TABLE SessionMemoryEmbeddingGenerations ADD COLUMN SourceFingerprint TEXT NULL;");
        }
        if (!HasColumn(connection, transaction, "SessionMemoryEmbeddingGenerations", "MaxCanonicalTextChars"))
        {
            Execute(
                connection,
                transaction,
                "ALTER TABLE SessionMemoryEmbeddingGenerations ADD COLUMN MaxCanonicalTextChars INTEGER NULL;");
        }
    }

    private static void AddMigrationIntegrityAndSecureErase(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (!HasColumn(connection, transaction, "MemorySchemaMigrations", "Checksum"))
        {
            Execute(
                connection,
                transaction,
                "ALTER TABLE MemorySchemaMigrations ADD COLUMN Checksum TEXT NULL;");
        }

        foreach (var migration in Migrations.Where(item => item.Version < MigrationIntegrityVersion))
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE MemorySchemaMigrations
                SET Checksum = $checksum
                WHERE Version = $version AND Name = $name;
                """;
            update.Parameters.AddWithValue("$checksum", migration.Checksum);
            update.Parameters.AddWithValue("$version", migration.Version);
            update.Parameters.AddWithValue("$name", migration.Name);
            if (update.ExecuteNonQuery() != 1)
            {
                throw InvalidLedger(
                    $"migration {migration.Version} could not be assigned its recognized checksum");
            }
        }

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SemanticMemoryMaintenance (
                Name TEXT PRIMARY KEY,
                CompletedAtUtc TEXT NOT NULL
            );
            """);
    }

    private static void EnableFts5SecureDelete(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ValidateFts5SecureDeleteSupport(connection, transaction);
        if (!HasTable(connection, transaction, "SessionMemorySearch"))
        {
            CreateSearchSchema(connection, transaction);
        }

        SetFts5SecureDelete(connection, transaction);
        RepairSearchIndex(connection, transaction);
        Execute(
            connection,
            transaction,
            "INSERT INTO SessionMemorySearch(SessionMemorySearch) VALUES('rebuild');");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SemanticDeletionMaintenance (
                EventId TEXT PRIMARY KEY,
                EventType TEXT NOT NULL CHECK (EventType IN (
                    'TranscriptRolledBack', 'SessionDeleted', 'WorkspaceDeleted')),
                PayloadHash TEXT NOT NULL CHECK (length(PayloadHash) = 64),
                State TEXT NOT NULL CHECK (State IN ('Pending', 'Completed')),
                CreatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                CHECK ((State = 'Pending' AND CompletedAtUtc IS NULL)
                    OR (State = 'Completed' AND CompletedAtUtc IS NOT NULL))
            );
            CREATE INDEX IF NOT EXISTS IX_SemanticDeletionMaintenance_State
                ON SemanticDeletionMaintenance (State, CreatedAtUtc);
            """);
    }

    private static void EnsureFts5SecureDeleteConfigured(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ValidateFts5SecureDeleteSupport(connection, transaction);
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT v FROM SessionMemorySearch_config WHERE k = 'secure-delete';";
        var enabled = query.ExecuteScalar();
        if (enabled is null || Convert.ToInt32(enabled) != 1)
        {
            SetFts5SecureDelete(connection, transaction);
        }
    }

    private static void SetFts5SecureDelete(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            "INSERT INTO SessionMemorySearch(SessionMemorySearch, rank) VALUES('secure-delete', 1);");
        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "SELECT v FROM SessionMemorySearch_config WHERE k = 'secure-delete';";
        if (Convert.ToInt32(verify.ExecuteScalar()) != 1)
        {
            throw new InvalidOperationException(
                "The bundled SQLite FTS5 engine did not persist table-level secure deletion.");
        }
    }

    private static void ValidateFts5SecureDeleteSupport(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sqlite_version(), sqlite_compileoption_used('ENABLE_FTS5');";
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !Version.TryParse(reader.GetString(0), out var sqliteVersion)
            || sqliteVersion < MinimumFtsSecureDeleteVersion
            || reader.GetInt32(1) != 1)
        {
            throw new InvalidOperationException(
                "Semantic Memory requires bundled SQLite 3.42.0 or later with FTS5 secure-delete support.");
        }
    }

    private static void CreateGenerationEmbeddingTable(SqliteConnection connection, SqliteTransaction transaction)
        => Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SessionMemoryEmbeddings (
                GenerationId TEXT NOT NULL,
                MemoryId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                CanonicalTextHash TEXT NOT NULL,
                Dimensions INTEGER NOT NULL,
                VectorJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (GenerationId, MemoryId)
            );
            """);

    private static void MigrateLegacyEmbeddings(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT SessionId, ProviderId, ModelId, COUNT(*), MIN(CreatedAtUtc), MAX(UpdatedAtUtc)
            FROM SessionMemoryEmbeddingsLegacy
            GROUP BY SessionId, ProviderId, ModelId;
            """;
        using var reader = select.ExecuteReader();
        var groups = new List<(string SessionId, string ProviderId, string ModelId, int Count, string CreatedAt, string UpdatedAt)>();
        while (reader.Read())
        {
            groups.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
        }

        foreach (var group in groups)
        {
            var generationId = Guid.NewGuid().ToString("N");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SessionMemoryEmbeddingGenerations (GenerationId, SessionId, ProviderId, ModelId, State, ExpectedMemoryCount, CreatedAtUtc, CompletedAtUtc)
                VALUES ($generationId, $sessionId, $providerId, $modelId, 'Complete', $count, $createdAtUtc, $completedAtUtc);
                INSERT INTO SessionMemoryEmbeddings (GenerationId, MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc)
                SELECT $generationId, MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc
                FROM SessionMemoryEmbeddingsLegacy
                WHERE SessionId = $sessionId AND ProviderId = $providerId AND ModelId = $modelId;
                INSERT INTO SessionMemoryActiveEmbeddingGenerations (SessionId, ProviderId, ModelId, GenerationId)
                VALUES ($sessionId, $providerId, $modelId, $generationId);
                """;
            command.Parameters.AddWithValue("$generationId", generationId);
            command.Parameters.AddWithValue("$sessionId", group.SessionId);
            command.Parameters.AddWithValue("$providerId", group.ProviderId);
            command.Parameters.AddWithValue("$modelId", group.ModelId);
            command.Parameters.AddWithValue("$count", group.Count);
            command.Parameters.AddWithValue("$createdAtUtc", group.CreatedAt);
            command.Parameters.AddWithValue("$completedAtUtc", group.UpdatedAt);
            command.ExecuteNonQuery();
        }
    }

    private static void RepairSearchIndex(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            DELETE FROM SessionMemorySearch;
            INSERT INTO SessionMemorySearch (MemoryId, SessionId, Category, Content, EvidenceText, State)
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, State
            FROM SessionMemories;
            """);
    }

    private static bool SearchIndexNeedsRepair(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN
                (SELECT COUNT(*) FROM SessionMemorySearch) <> (SELECT COUNT(*) FROM SessionMemories)
                OR EXISTS (
                    SELECT MemoryId, SessionId, Category, Content, EvidenceText, State FROM SessionMemories
                    EXCEPT
                    SELECT MemoryId, SessionId, Category, Content, EvidenceText, State FROM SessionMemorySearch
                )
                OR EXISTS (
                    SELECT MemoryId, SessionId, Category, Content, EvidenceText, State FROM SessionMemorySearch
                    EXCEPT
                    SELECT MemoryId, SessionId, Category, Content, EvidenceText, State FROM SessionMemories
                )
                THEN 1 ELSE 0 END;
            """;
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static bool HasTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void EnsureSecureEraseMaintenance()
    {
        using var maintenanceLock = MemoryDatabase.AcquireMaintenanceLock(_databasePath);
        using var connection = MemoryDatabase.OpenConnection(_databasePath);
        var pending = new[] { LegacySecureEraseMaintenance, FtsSecureEraseMaintenance }
            .Where(name => !HasCompletedMaintenance(connection, name))
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        MemoryDatabase.SecurePurge(connection);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        foreach (var name in pending)
        {
            using var complete = connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText = """
                INSERT INTO SemanticMemoryMaintenance (Name, CompletedAtUtc)
                VALUES ($name, $completedAtUtc);
                """;
            complete.Parameters.AddWithValue("$name", name);
            complete.Parameters.AddWithValue("$completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            if (complete.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Semantic memory secure-erasure maintenance '{name}' could not be recorded.");
            }
        }
        transaction.Commit();
    }

    private static bool HasCompletedMaintenance(SqliteConnection connection, string name)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM SemanticMemoryMaintenance WHERE Name = $name LIMIT 1;";
        check.Parameters.AddWithValue("$name", name);
        return check.ExecuteScalar() is not null;
    }

    private static InvalidOperationException InvalidLedger(string reason)
        => new($"Semantic memory migration ledger validation failed: {reason}. "
            + "Back up 'memory/agent-memory.db' and use a compatible Semantic Memory package; "
            + "do not edit or delete migration ledger rows.");

    private sealed record SchemaMigration(
        int Version,
        string Name,
        string ChecksumSource,
        Action<SqliteConnection, SqliteTransaction> Apply)
    {
        public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Version}\n{Name}\n{ChecksumSource}"))).ToLowerInvariant();
    }

    private sealed record MigrationLedgerEntry(int Version, string Name, string? Checksum);
}

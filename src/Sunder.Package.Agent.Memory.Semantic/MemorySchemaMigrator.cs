using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Shared.Threading;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class MemorySchemaMigrator(string databasePath)
{
    internal const int CurrentVersion = 5;

    private static readonly ReferenceCountedKeyedLock<string> MigrationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _databasePath = databasePath;

    public void Migrate()
    {
        using (MigrationLocks.Enter(Path.GetFullPath(_databasePath)))
        {
            using var connection = MemoryDatabase.OpenConnection(_databasePath);
            EnableWal(connection);
            EnsureLedger(connection);

            var appliedVersions = ReadAppliedVersions(connection);
            ValidateLedger(appliedVersions);
            ApplyMigration(connection, appliedVersions, 1, "Create memory, evidence, and embedding tables", CreateInitialSchema);
            ApplyMigration(connection, appliedVersions, 2, "Add memory supersession lineage", AddSupersessionColumn);
            ApplyMigration(connection, appliedVersions, 3, "Create and populate full-text search", CreateSearchSchema);
            ApplyMigration(connection, appliedVersions, 4, "Add staged embedding generations", AddEmbeddingGenerations);
            ApplyMigration(connection, appliedVersions, 5, "Add memory provenance", AddMemoryProvenance);

            if (!HasTable(connection, "SessionMemorySearch"))
            {
                using var recreateTransaction = connection.BeginTransaction();
                CreateSearchSchema(connection, recreateTransaction);
                recreateTransaction.Commit();
            }
            else if (SearchIndexNeedsRepair(connection))
            {
                using var repairTransaction = connection.BeginTransaction();
                RepairSearchIndex(connection, repairTransaction);
                repairTransaction.Commit();
            }
        }
    }

    private static void EnableWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }

    private static void EnsureLedger(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
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
        transaction.Commit();
    }

    private static HashSet<int> ReadAppliedVersions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM MemorySchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var versions = new HashSet<int>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static void ValidateLedger(IReadOnlySet<int> versions)
    {
        if (versions.Any(version => version < 1 || version > CurrentVersion))
        {
            throw new InvalidOperationException("The semantic memory database was created by an unsupported schema version.");
        }

        var highestVersion = versions.Count == 0 ? 0 : versions.Max();
        for (var version = 1; version <= highestVersion; version++)
        {
            if (!versions.Contains(version))
            {
                throw new InvalidOperationException($"The semantic memory migration ledger is missing version {version}.");
            }
        }
    }

    private static void ApplyMigration(
        SqliteConnection connection,
        ISet<int> appliedVersions,
        int version,
        string name,
        Action<SqliteConnection, SqliteTransaction> migration)
    {
        if (appliedVersions.Contains(version))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        migration(connection, transaction);
        using var ledgerCommand = connection.CreateCommand();
        ledgerCommand.Transaction = transaction;
        ledgerCommand.CommandText = """
            INSERT INTO MemorySchemaMigrations (Version, Name, AppliedAtUtc)
            VALUES ($version, $name, $appliedAtUtc);
            """;
        ledgerCommand.Parameters.AddWithValue("$version", version);
        ledgerCommand.Parameters.AddWithValue("$name", name);
        ledgerCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        ledgerCommand.ExecuteNonQuery();
        transaction.Commit();
        appliedVersions.Add(version);
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

    private static bool SearchIndexNeedsRepair(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
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

    private static bool HasTable(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
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
}

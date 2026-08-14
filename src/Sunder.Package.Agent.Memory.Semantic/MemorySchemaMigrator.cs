using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.Memory.Semantic;

internal sealed class MemorySchemaMigrator(string databasePath)
{
    internal const int CurrentVersion = 1;
    internal const string BaselineName = "semantic-memory-2.0-baseline";

    private static readonly Version MinimumFtsSecureDeleteVersion = new(3, 42, 0);

    private const string BaselineSql = """
        CREATE TABLE MemorySchemaMigrations (
            Version INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            Checksum TEXT NOT NULL,
            AppliedAtUtc TEXT NOT NULL
        );

        CREATE TABLE SessionMemories (
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
            Provenance TEXT NOT NULL DEFAULT 'Unknown',
            SupersededByMemoryId TEXT NULL,
            MemoryRevision INTEGER NOT NULL DEFAULT 1,
            IsManual INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE SessionMemoryEvidence (
            EvidenceId TEXT PRIMARY KEY,
            MemoryId TEXT NOT NULL,
            SessionId TEXT NOT NULL,
            SourceTurnId TEXT NULL,
            EvidenceText TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            ContributionId TEXT NULL
        );

        CREATE TABLE SessionMemoryEmbeddingGenerations (
            GenerationId TEXT PRIMARY KEY,
            SessionId TEXT NOT NULL,
            ProviderId TEXT NOT NULL,
            ModelId TEXT NOT NULL,
            State TEXT NOT NULL,
            ExpectedMemoryCount INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            CompletedAtUtc TEXT NULL,
            ConfigurationFingerprint TEXT NULL,
            SourceFingerprint TEXT NULL,
            MaxCanonicalTextChars INTEGER NULL
        );

        CREATE TABLE SessionMemoryActiveEmbeddingGenerations (
            SessionId TEXT NOT NULL,
            ProviderId TEXT NOT NULL,
            ModelId TEXT NOT NULL,
            GenerationId TEXT NOT NULL,
            PRIMARY KEY (SessionId, ProviderId, ModelId)
        );

        CREATE TABLE SessionMemoryEmbeddings (
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
            MemoryRevision INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (GenerationId, MemoryId)
        );

        CREATE VIRTUAL TABLE SessionMemorySearch USING fts5(
            MemoryId UNINDEXED,
            SessionId UNINDEXED,
            Category,
            Content,
            EvidenceText,
            State UNINDEXED
        );
        INSERT INTO SessionMemorySearch(SessionMemorySearch, rank) VALUES('secure-delete', 1);

        CREATE TABLE SemanticLifecycleInbox (
            EventId TEXT PRIMARY KEY,
            EventType TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK (length(PayloadHash) = 64),
            SourceKey TEXT NOT NULL,
            EventSequence INTEGER NOT NULL,
            ReceivedAtUtc TEXT NOT NULL
        );

        CREATE TABLE SessionMemoryContributions (
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

        CREATE TABLE SessionMemoryTurnRetractions (
            SessionId TEXT NOT NULL,
            TurnId TEXT NOT NULL,
            EventId TEXT NOT NULL,
            PayloadHash TEXT NOT NULL,
            RetractedAtUtc TEXT NOT NULL,
            PRIMARY KEY (SessionId, TurnId)
        );

        CREATE TABLE SessionMemoryDeletionTombstones (
            SessionId TEXT PRIMARY KEY,
            WorkspaceId TEXT NULL,
            EventId TEXT NOT NULL,
            PayloadHash TEXT NOT NULL,
            DeletedAtUtc TEXT NOT NULL
        );

        CREATE TABLE WorkspaceMemoryDeletionTombstones (
            WorkspaceId TEXT PRIMARY KEY,
            EventId TEXT NOT NULL,
            PayloadHash TEXT NOT NULL,
            DeletedAtUtc TEXT NOT NULL
        );

        CREATE TABLE SemanticMemoryMaintenance (
            Name TEXT PRIMARY KEY,
            CompletedAtUtc TEXT NOT NULL
        );

        CREATE TABLE SemanticDeletionMaintenance (
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

        CREATE UNIQUE INDEX UX_SessionMemories_SessionId_Category_NormalizedContent
            ON SessionMemories (SessionId, Category, NormalizedContent);
        CREATE INDEX IX_SessionMemories_SessionId_State_UpdatedAtUtc
            ON SessionMemories (SessionId, State, UpdatedAtUtc DESC);
        CREATE INDEX IX_SessionMemoryEvidence_MemoryId_CreatedAtUtc
            ON SessionMemoryEvidence (MemoryId, CreatedAtUtc DESC);
        CREATE UNIQUE INDEX UX_SessionMemoryEvidence_ContributionId
            ON SessionMemoryEvidence (ContributionId)
            WHERE ContributionId IS NOT NULL;
        CREATE INDEX IX_SessionMemoryEmbeddings_SessionId_ProviderId_ModelId
            ON SessionMemoryEmbeddings (SessionId, ProviderId, ModelId);
        CREATE INDEX IX_SessionMemoryEmbeddingGenerations_SessionProviderModel
            ON SessionMemoryEmbeddingGenerations (SessionId, ProviderId, ModelId, State);
        CREATE INDEX IX_SessionMemoryContributions_SourceTurn
            ON SessionMemoryContributions (SessionId, SourceTurnId);
        CREATE INDEX IX_SessionMemoryContributions_Memory
            ON SessionMemoryContributions (MemoryId, CreatedAtUtc);
        CREATE INDEX IX_SemanticLifecycleInbox_Sequence
            ON SemanticLifecycleInbox (EventSequence);
        CREATE INDEX IX_SemanticDeletionMaintenance_State
            ON SemanticDeletionMaintenance (State, CreatedAtUtc);
        """;

    internal const string BaselineChecksum = "72c53e869dbc6dcbd56c6376ce8b1721b3b9152b58c1191e3151968f7d921a";

    private readonly string _databasePath = databasePath;

    public void Migrate()
    {
        using var connection = new SqliteConnection(MemoryDatabase.CreateConnectionString(_databasePath));
        connection.Open();
        if (IsEmptyDatabase(connection))
        {
            InitializeEmptyDatabase(connection);
        }
        else
        {
            ValidateCurrentLedger(connection);
        }

        EnableWal(connection);
        EnableSecureDelete(connection);
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        ValidateFts5SecureDeleteSupport(connection, transaction);
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

    private static void InitializeEmptyDatabase(SqliteConnection connection)
    {
        using var transaction = MemoryDatabase.BeginImmediateTransaction(connection);
        if (!IsEmptyDatabase(connection, transaction))
        {
            ValidateCurrentLedger(connection, transaction);
            transaction.Commit();
            return;
        }

        ValidateFts5SecureDeleteSupport(connection, transaction);
        Execute(connection, transaction, BaselineSql);
        using var ledger = connection.CreateCommand();
        ledger.Transaction = transaction;
        ledger.CommandText = """
            INSERT INTO MemorySchemaMigrations (Version, Name, Checksum, AppliedAtUtc)
            VALUES ($version, $name, $checksum, $appliedAtUtc);
            """;
        ledger.Parameters.AddWithValue("$version", CurrentVersion);
        ledger.Parameters.AddWithValue("$name", BaselineName);
        ledger.Parameters.AddWithValue("$checksum", BaselineChecksum);
        ledger.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        ledger.ExecuteNonQuery();
        transaction.Commit();
    }

    private static bool IsEmptyDatabase(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' LIMIT 1;";
        return command.ExecuteScalar() is null;
    }

    private static void ValidateCurrentLedger(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        try
        {
            using var columns = connection.CreateCommand();
            columns.Transaction = transaction;
            columns.CommandText = "PRAGMA table_info(MemorySchemaMigrations);";
            using var columnReader = columns.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (columnReader.Read()) names.Add(columnReader.GetString(1));
            if (!names.SetEquals(["Version", "Name", "Checksum", "AppliedAtUtc"]))
            {
                throw InvalidLedger("the ledger table does not have the exact current columns");
            }

            using var ledger = connection.CreateCommand();
            ledger.Transaction = transaction;
            ledger.CommandText = "SELECT Version, Name, Checksum FROM MemorySchemaMigrations ORDER BY Version;";
            using var reader = ledger.ExecuteReader();
            if (!reader.Read()) throw InvalidLedger("the current baseline entry is missing");
            var version = reader.GetInt32(0);
            var name = reader.GetString(1);
            var checksum = reader.GetString(2);
            if (reader.Read()) throw InvalidLedger("the ledger contains more than one entry");
            if (version != CurrentVersion
                || !string.Equals(name, BaselineName, StringComparison.Ordinal)
                || !string.Equals(checksum, BaselineChecksum, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidLedger("the baseline identity or checksum does not match this build");
            }
        }
        catch (InvalidOperationException exception) when (!exception.Message.StartsWith(
                   "Semantic memory migration ledger validation failed:",
                   StringComparison.Ordinal))
        {
            throw InvalidLedger("the current ledger could not be read", exception);
        }
        catch (SqliteException exception)
        {
            throw InvalidLedger("the current ledger could not be read", exception);
        }
    }

    private static void EnableWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = command.ExecuteScalar() as string;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Semantic Memory could not enable SQLite WAL mode; journal mode remained '{mode ?? "unknown"}'.");
        }
    }

    private static void EnableSecureDelete(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
    }

    private static void CreateSearchSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE VIRTUAL TABLE SessionMemorySearch USING fts5(
                MemoryId UNINDEXED,
                SessionId UNINDEXED,
                Category,
                Content,
                EvidenceText,
                State UNINDEXED
            );
            """);
        SetFts5SecureDelete(connection, transaction);
        RepairSearchIndex(connection, transaction);
    }

    private static void EnsureFts5SecureDeleteConfigured(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT v FROM SessionMemorySearch_config WHERE k = 'secure-delete';";
        if (query.ExecuteScalar() is not { } enabled || Convert.ToInt32(enabled) != 1)
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

    private static void RepairSearchIndex(SqliteConnection connection, SqliteTransaction transaction)
        => Execute(connection, transaction, """
            DELETE FROM SessionMemorySearch;
            INSERT INTO SessionMemorySearch (MemoryId, SessionId, Category, Content, EvidenceText, State)
            SELECT MemoryId, SessionId, Category, Content, EvidenceText, State
            FROM SessionMemories;
            """);

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

    private static InvalidOperationException InvalidLedger(string reason, Exception? innerException = null)
        => new(
            $"Semantic memory migration ledger validation failed: {reason}. "
            + "Only an absent or empty database, or the exact current one-entry ledger, is supported; "
            + "do not edit or delete migration ledger rows.",
            innerException);
}

using Microsoft.Data.Sqlite;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchStore
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["HistorySchemaMarker"] = ["Id", "Version", "CreatedAtUtc"],
            ["HistoryProjectionGenerations"] =
            [
                "GenerationId", "ProjectionKind", "State", "ParentTextGenerationId",
                "ExtractorVersion", "RedactionVersion", "ProviderPackageId", "ProviderId",
                "ModelId", "ConfigurationRevision", "EmbeddingSpaceFingerprint", "Dimensions",
                "StartedAtUtc", "CompletedAtUtc", "FailureCode", "RuntimeEpoch",
            ],
            ["HistoryProjectionState"] =
            [
                "Id", "ActiveTextGenerationId", "ActiveEmbeddingGenerationId", "SemanticReady",
                "IsManuallyCleared", "LastReconciledAtUtc", "RuntimeEpoch",
            ],
            ["HistoryConfiguration"] =
            [
                "Id", "SemanticEnabled", "EmbeddingProviderPackageId", "EmbeddingProviderId",
                "EmbeddingModelId", "Revision", "EmbeddingSpaceFingerprint", "UpdatedAtUtc",
            ],
            ["HistoryDocuments"] =
            [
                "GenerationId", "DocumentId", "WorkspaceId", "WorkspaceName", "SessionId",
                "SessionTitle", "RootSessionId", "ParentSessionId", "ProfileId", "Role",
                "Activity", "TurnId", "ItemId", "CallId", "AnchorKind",
                "SourceContentRevision", "SourceIsStreaming", "CreatedAtUtc", "BodyText",
                "DisplaySnippet", "ProjectionHash",
            ],
            ["HistoryDocumentFacets"] =
            ["GenerationId", "DocumentId", "Kind", "Value", "NormalizedValue"],
            ["HistoryEmbeddings"] =
            [
                "EmbeddingGenerationId", "DocumentId", "TextGenerationId", "ProviderPackageId",
                "ProviderId", "ModelId", "ConfigurationRevision", "EmbeddingSpaceFingerprint",
                "DocumentProjectionHash", "Dimensions", "Vector",
            ],
        };

    private void InitializeWithRecovery()
    {
        if (!File.Exists(DatabasePath))
        {
            CreateSchema(runtimeEpoch: null, configurationRevision: 0, manuallyCleared: false);
            return;
        }

        try
        {
            ValidateExistingSchema();
        }
        catch (Exception exception) when (IsConfirmedProjectionFailure(exception))
        {
            RecreateDerivedDatabase(configurationRevision: 0, manuallyCleared: false);
            WasRecovered = true;
        }
    }

    private void ValidateExistingSchema()
    {
        using var connection = CreateConnection();
        connection.Open();
        EnableWriteAheadLogging(connection);
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT Version FROM HistorySchemaMarker WHERE Id = 1;";
            var existingVersion = Convert.ToInt32(version.ExecuteScalar());
            if (existingVersion != HistorySearchVersions.Schema)
            {
                if (existingVersion > 0 && existingVersion < HistorySearchVersions.Schema)
                {
                    _requiresSecureRecreation = true;
                    return;
                }
                throw new InvalidDataException("History projection schema is unsupported.");
            }
        }
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("History projection integrity check failed.");
            }
        }

        foreach (var (table, requiredColumns) in RequiredColumns)
        {
            var columns = ListColumns(connection, table);
            if (!requiredColumns.All(columns.Contains))
            {
                throw new InvalidDataException($"History projection table '{table}' is incomplete.");
            }
        }
        EnsureTableExists(connection, "HistoryDocumentsFts");
        EnsureSingleton(connection, "HistorySchemaMarker");
        EnsureSingleton(connection, "HistoryProjectionState");
        EnsureSingleton(connection, "HistoryConfiguration");

        using (var invariant = connection.CreateCommand())
        {
            invariant.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE ProjectionKind = 'Text' AND State = 'Active') > 1
                 OR (SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE ProjectionKind = 'Embedding' AND State = 'Active') > 1
                 OR EXISTS (
                    SELECT 1 FROM HistoryProjectionState s
                     LEFT JOIN HistoryProjectionGenerations g ON g.GenerationId = s.ActiveTextGenerationId
                     WHERE s.ActiveTextGenerationId IS NOT NULL
                       AND (g.GenerationId IS NULL OR g.ProjectionKind <> 'Text' OR g.State <> 'Active'
                            OR g.RuntimeEpoch IS NOT s.RuntimeEpoch))
                 OR EXISTS (
                    SELECT 1 FROM HistoryProjectionState s
                    LEFT JOIN HistoryProjectionGenerations g ON g.GenerationId = s.ActiveEmbeddingGenerationId
                    WHERE s.ActiveEmbeddingGenerationId IS NOT NULL
                       AND (g.GenerationId IS NULL OR g.ProjectionKind <> 'Embedding' OR g.State <> 'Active'
                            OR g.ParentTextGenerationId <> s.ActiveTextGenerationId
                            OR g.RuntimeEpoch IS NOT s.RuntimeEpoch))
                 OR EXISTS (
                    SELECT 1 FROM HistoryDocuments d
                    LEFT JOIN HistoryProjectionGenerations g ON g.GenerationId = d.GenerationId
                    WHERE g.GenerationId IS NULL OR g.ProjectionKind <> 'Text')
                 OR EXISTS (
                    SELECT 1 FROM HistoryDocumentFacets f
                    LEFT JOIN HistoryDocuments d
                      ON d.GenerationId = f.GenerationId AND d.DocumentId = f.DocumentId
                    WHERE d.DocumentId IS NULL)
                 OR EXISTS (
                    SELECT 1 FROM HistoryEmbeddings e
                    LEFT JOIN HistoryProjectionGenerations g
                      ON g.GenerationId = e.EmbeddingGenerationId
                    LEFT JOIN HistoryDocuments d
                      ON d.GenerationId = e.TextGenerationId AND d.DocumentId = e.DocumentId
                    WHERE g.GenerationId IS NULL OR g.ProjectionKind <> 'Embedding'
                       OR d.DocumentId IS NULL
                       OR e.TextGenerationId <> g.ParentTextGenerationId
                       OR e.ProviderPackageId <> g.ProviderPackageId COLLATE NOCASE
                       OR e.ProviderId <> g.ProviderId COLLATE NOCASE
                        OR e.ModelId <> g.ModelId COLLATE BINARY
                       OR e.ConfigurationRevision <> g.ConfigurationRevision
                       OR e.EmbeddingSpaceFingerprint <> g.EmbeddingSpaceFingerprint
                       OR e.DocumentProjectionHash <> d.ProjectionHash
                       OR e.Dimensions <= 0 OR e.Dimensions > $maximumDimensions
                       OR length(e.Vector) <> e.Dimensions * 4)
                 OR EXISTS (
                    SELECT DocumentId, CAST(GenerationId AS INTEGER) FROM HistoryDocumentsFts
                    EXCEPT SELECT DocumentId, GenerationId FROM HistoryDocuments)
                 OR EXISTS (
                    SELECT DocumentId, GenerationId FROM HistoryDocuments
                    EXCEPT SELECT DocumentId, CAST(GenerationId AS INTEGER) FROM HistoryDocumentsFts);
                """;
            invariant.Parameters.AddWithValue("$maximumDimensions", HistorySearchLimits.MaximumVectorDimensions);
            if (Convert.ToInt64(invariant.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("History projection invariants are invalid.");
            }
        }

        ValidateFtsOperations(connection);
        _ftsSecureDeleteEnabled = IsFtsSecureDeleteEnabled(connection);
    }

    private void CreateSchema(
        Guid? runtimeEpoch,
        long configurationRevision,
        bool manuallyCleared,
        string? databasePath = null)
    {
        using var connection = CreateConnection(databasePath);
        connection.Open();
        EnableSecureDelete(connection);
        EnableWriteAheadLogging(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE HistorySchemaMarker (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE HistoryProjectionGenerations (
                GenerationId INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectionKind TEXT NOT NULL CHECK (ProjectionKind IN ('Text', 'Embedding')),
                State TEXT NOT NULL CHECK (State IN ('Staging', 'Active', 'Superseded', 'Failed')),
                ParentTextGenerationId INTEGER NULL,
                ExtractorVersion INTEGER NOT NULL,
                RedactionVersion INTEGER NOT NULL,
                ProviderPackageId TEXT NULL,
                ProviderId TEXT NULL,
                ModelId TEXT NULL,
                ConfigurationRevision INTEGER NOT NULL,
                EmbeddingSpaceFingerprint TEXT NULL,
                Dimensions INTEGER NULL,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FailureCode TEXT NULL,
                RuntimeEpoch TEXT NULL
            );

            CREATE TABLE HistoryProjectionState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                ActiveTextGenerationId INTEGER NULL,
                ActiveEmbeddingGenerationId INTEGER NULL,
                SemanticReady INTEGER NOT NULL DEFAULT 0 CHECK (SemanticReady IN (0, 1)),
                IsManuallyCleared INTEGER NOT NULL DEFAULT 0 CHECK (IsManuallyCleared IN (0, 1)),
                LastReconciledAtUtc TEXT NULL,
                RuntimeEpoch TEXT NULL
            );

            CREATE TABLE HistoryConfiguration (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                SemanticEnabled INTEGER NOT NULL DEFAULT 0 CHECK (SemanticEnabled IN (0, 1)),
                EmbeddingProviderPackageId TEXT NULL,
                EmbeddingProviderId TEXT NULL,
                EmbeddingModelId TEXT NULL,
                Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0),
                EmbeddingSpaceFingerprint TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE HistoryDocuments (
                GenerationId INTEGER NOT NULL,
                DocumentId TEXT NOT NULL,
                WorkspaceId TEXT NOT NULL,
                WorkspaceName TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                SessionTitle TEXT NOT NULL,
                RootSessionId TEXT NULL,
                ParentSessionId TEXT NULL,
                ProfileId TEXT NULL,
                Role TEXT NULL,
                Activity TEXT NOT NULL,
                TurnId TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                CallId TEXT NULL,
                AnchorKind TEXT NOT NULL,
                SourceContentRevision INTEGER NOT NULL,
                SourceIsStreaming INTEGER NOT NULL CHECK (SourceIsStreaming IN (0, 1)),
                CreatedAtUtc TEXT NOT NULL,
                BodyText TEXT NOT NULL,
                DisplaySnippet TEXT NOT NULL,
                ProjectionHash TEXT NOT NULL CHECK (length(ProjectionHash) = 64),
                PRIMARY KEY (GenerationId, DocumentId)
            );

            CREATE TABLE HistoryDocumentFacets (
                GenerationId INTEGER NOT NULL,
                DocumentId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Value TEXT NOT NULL,
                NormalizedValue TEXT NOT NULL,
                PRIMARY KEY (GenerationId, DocumentId, Kind, NormalizedValue)
            );

            CREATE VIRTUAL TABLE HistoryDocumentsFts USING fts5(
                DocumentId UNINDEXED,
                GenerationId UNINDEXED,
                BodyText,
                PathText,
                SymbolText,
                ActivityText,
                ToolText,
                tokenize = 'unicode61 remove_diacritics 2'
            );

            CREATE TABLE HistoryEmbeddings (
                EmbeddingGenerationId INTEGER NOT NULL,
                DocumentId TEXT NOT NULL,
                TextGenerationId INTEGER NOT NULL,
                ProviderPackageId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                ConfigurationRevision INTEGER NOT NULL CHECK (ConfigurationRevision >= 0),
                EmbeddingSpaceFingerprint TEXT NOT NULL CHECK (length(EmbeddingSpaceFingerprint) = 64),
                DocumentProjectionHash TEXT NOT NULL CHECK (length(DocumentProjectionHash) = 64),
                Dimensions INTEGER NOT NULL CHECK (Dimensions > 0 AND Dimensions <= 8192),
                Vector BLOB NOT NULL CHECK (length(Vector) = Dimensions * 4),
                PRIMARY KEY (EmbeddingGenerationId, DocumentId)
            );

            CREATE INDEX IX_HistoryDocuments_Scope
                ON HistoryDocuments (GenerationId, WorkspaceId, SessionId, ProfileId, CreatedAtUtc);
            CREATE INDEX IX_HistoryDocuments_Parent
                ON HistoryDocuments (GenerationId, ParentSessionId, SessionId);
            CREATE INDEX IX_HistoryDocuments_Turn
                ON HistoryDocuments (GenerationId, SessionId, TurnId);
            CREATE INDEX IX_HistoryDocuments_Activity
                ON HistoryDocuments (GenerationId, Activity, CreatedAtUtc);
            CREATE INDEX IX_HistoryDocumentFacets_Exact
                ON HistoryDocumentFacets (GenerationId, Kind, NormalizedValue, DocumentId);
            CREATE INDEX IX_HistoryEmbeddings_Scan
                ON HistoryEmbeddings (EmbeddingGenerationId, DocumentId);

            INSERT INTO HistorySchemaMarker (Id, Version, CreatedAtUtc) VALUES (1, $version, $now);
            INSERT INTO HistoryProjectionState (Id, IsManuallyCleared, RuntimeEpoch)
            VALUES (1, $manuallyCleared, $runtimeEpoch);
            INSERT INTO HistoryConfiguration (Id, Revision, UpdatedAtUtc)
            VALUES (1, $configurationRevision, $now);
            """;
        command.Parameters.AddWithValue("$version", HistorySearchVersions.Schema);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$manuallyCleared", manuallyCleared ? 1 : 0);
        command.Parameters.AddWithValue("$runtimeEpoch", runtimeEpoch?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$configurationRevision", configurationRevision);
        command.ExecuteNonQuery();
        transaction.Commit();
        _ftsSecureDeleteEnabled = TryEnableFtsSecureDelete(connection);
        CheckpointTruncate(connection);
    }

    private static void ReclaimAbandonedGenerations(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM HistoryEmbeddings
                WHERE EmbeddingGenerationId IN (
                    SELECT GenerationId FROM HistoryProjectionGenerations
                    WHERE State IN ('Staging', 'Failed'))
                   OR TextGenerationId IN (
                    SELECT GenerationId FROM HistoryProjectionGenerations
                    WHERE ProjectionKind = 'Text' AND State IN ('Staging', 'Failed'));
                DELETE FROM HistoryDocumentsFts
                WHERE CAST(GenerationId AS INTEGER) IN (
                    SELECT GenerationId FROM HistoryProjectionGenerations
                    WHERE ProjectionKind = 'Text' AND State IN ('Staging', 'Failed'));
                DELETE FROM HistoryDocumentFacets
                WHERE GenerationId IN (
                    SELECT GenerationId FROM HistoryProjectionGenerations
                    WHERE ProjectionKind = 'Text' AND State IN ('Staging', 'Failed'));
                DELETE FROM HistoryDocuments
                WHERE GenerationId IN (
                    SELECT GenerationId FROM HistoryProjectionGenerations
                    WHERE ProjectionKind = 'Text' AND State IN ('Staging', 'Failed'));
                DELETE FROM HistoryProjectionGenerations WHERE State IN ('Staging', 'Failed');
                """;
            command.ExecuteNonQuery();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }

    private SqliteConnection CreateConnection(string? databasePath = null)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? DatabasePath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString());

    private static void EnableWriteAheadLogging(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteScalar();
    }

    private static HashSet<string> ListColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''", StringComparison.Ordinal)}');";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static void EnsureTableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
        {
            throw new InvalidDataException($"History projection table '{table}' is missing.");
        }
    }

    private static void EnsureSingleton(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*), COALESCE(MIN(Id), 0), COALESCE(MAX(Id), 0) FROM {table};";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1 || reader.GetInt64(2) != 1)
        {
            throw new InvalidDataException($"History projection singleton '{table}' is invalid.");
        }
    }

    private static void ValidateFtsOperations(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO HistoryDocumentsFts
                    (DocumentId, GenerationId, BodyText, PathText, SymbolText, ActivityText, ToolText)
                VALUES ('schema-probe', '-1', 'historyschemaoperationprobe', '', '', '', '');
                """;
            insert.ExecuteNonQuery();
        }
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT COUNT(*) FROM HistoryDocumentsFts WHERE HistoryDocumentsFts MATCH 'historyschemaoperationprobe';";
            if (Convert.ToInt32(query.ExecuteScalar()) != 1)
            {
                throw new InvalidDataException("History projection FTS operations are unavailable.");
            }
        }
        transaction.Rollback();
    }

    internal static bool IsConfirmedProjectionFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }
        if (exception is InvalidDataException)
        {
            return true;
        }
        if (exception is not SqliteException sqlite)
        {
            return false;
        }

        if (sqlite.SqliteErrorCode is 11 or 17 or 26)
        {
            return true;
        }
        if (sqlite.SqliteErrorCode != 1)
        {
            return false;
        }

        var message = sqlite.Message;
        return message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
               || message.Contains("malformed database schema", StringComparison.OrdinalIgnoreCase)
               || message.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no such module: fts5", StringComparison.OrdinalIgnoreCase);
    }

    private long BeginGeneration(
        string projectionKind,
        long? parentTextGenerationId,
        HistoryProjectionConfiguration configuration)
    {
        lock (_writeLock)
        {
            using var connection = CreateConnection();
            connection.Open();
            using var transaction = BeginMutation(connection, "begin-generation");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO HistoryProjectionGenerations (
                    ProjectionKind, State, ParentTextGenerationId, ExtractorVersion, RedactionVersion,
                    ProviderPackageId, ProviderId, ModelId, ConfigurationRevision,
                    EmbeddingSpaceFingerprint, StartedAtUtc, RuntimeEpoch)
                VALUES ($kind, 'Staging', $parentTextGenerationId, $extractorVersion, $redactionVersion,
                        $providerPackageId, $providerId, $modelId, $configurationRevision,
                        $spaceFingerprint, $startedAtUtc, $runtimeEpoch);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$kind", projectionKind);
            command.Parameters.AddWithValue("$parentTextGenerationId", (object?)parentTextGenerationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$extractorVersion", HistorySearchVersions.Extractor);
            command.Parameters.AddWithValue("$redactionVersion", HistorySearchVersions.Redaction);
            command.Parameters.AddWithValue("$providerPackageId", (object?)configuration.EmbeddingProviderPackageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$providerId", (object?)configuration.EmbeddingProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$modelId", (object?)configuration.EmbeddingModelId ?? DBNull.Value);
            command.Parameters.AddWithValue("$configurationRevision", configuration.Revision);
            command.Parameters.AddWithValue("$spaceFingerprint", (object?)configuration.EmbeddingSpaceFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$startedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$runtimeEpoch", GetBoundRuntimeEpoch()?.ToString() ?? (object)DBNull.Value);
            var generationId = Convert.ToInt64(command.ExecuteScalar());
            CommitMutation(connection, transaction);
            return generationId;
        }
    }

    private HistoryProjectionGeneration? GetGeneration(long? generationId)
    {
        if (generationId is null || !IsAvailable)
        {
            return null;
        }
        using var connection = CreateConnection();
        connection.Open();
        return GetGeneration(connection, transaction: null, generationId.Value);
    }

    private static HistoryProjectionGeneration? GetGeneration(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT GenerationId, ProjectionKind, State, ParentTextGenerationId,
                   ExtractorVersion, RedactionVersion, ProviderPackageId, ProviderId, ModelId,
                   Dimensions, ConfigurationRevision, EmbeddingSpaceFingerprint, RuntimeEpoch
            FROM HistoryProjectionGenerations
            WHERE GenerationId = $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new HistoryProjectionGeneration(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)))
            : null;
    }

    private void EnsureTextGenerationWritable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId)
    {
        var generation = GetGeneration(connection, transaction, generationId)
            ?? throw new InvalidOperationException("The text projection generation was not found.");
        if (generation.ProjectionKind != "Text" || generation.State is not ("Staging" or "Active"))
        {
            throw new InvalidOperationException("The text projection generation is not writable.");
        }
        EnsureGenerationRuntimeEpoch(generation);
    }

    private HistoryProjectionGeneration EnsureStagingGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        string projectionKind)
    {
        var generation = GetGeneration(connection, transaction, generationId)
            ?? throw new InvalidOperationException("The projection generation was not found.");
        if (generation.ProjectionKind != projectionKind || generation.State != "Staging")
        {
            throw new InvalidOperationException("The projection generation is not staging.");
        }
        EnsureGenerationRuntimeEpoch(generation);
        return generation;
    }

    private void CompleteGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        int? dimensions)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE HistoryProjectionGenerations
            SET State = 'Active', Dimensions = $dimensions, CompletedAtUtc = $completedAtUtc, FailureCode = NULL
            WHERE GenerationId = $generationId AND State = 'Staging'
              AND RuntimeEpoch IS $runtimeEpoch;
            """;
        command.Parameters.AddWithValue("$dimensions", (object?)dimensions ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$runtimeEpoch", GetBoundRuntimeEpoch()?.ToString() ?? (object)DBNull.Value);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The projection generation could not be activated.");
        }
    }

    private static long? GetActiveTextGenerationId(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ActiveTextGenerationId FROM HistoryProjectionState WHERE Id = 1;";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static IReadOnlyList<long> ListTextGenerationIds(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT GenerationId FROM HistoryProjectionGenerations WHERE ProjectionKind = 'Text';";
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
        {
            values.Add(reader.GetInt64(0));
        }
        return values;
    }
}

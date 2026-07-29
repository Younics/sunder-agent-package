using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class MemoryLocalStorePersistenceTests
{
    [Fact]
    public void ReleasedMigrationIdentities_OneThroughElevenMatchGoldenChecksums()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        using var connection = OpenConnection(storage.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM MemorySchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
        {
            actual.Add($"{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        }

        Assert.Equal(
            """
            1|Create memory, evidence, and embedding tables|48b910dfca11e88277a82672f9d9f9ffb31c5f4170dea30f778d1eb819e8959c
            2|Add memory supersession lineage|e0aee46f3a29b08c71df9a5cf3da0f897a50e4a0239158af7374264491226191
            3|Create and populate full-text search|4ebbb1bd7d97da962a55cebadb590b2db93e1c2aab477d533dfe8f97289a32d0
            4|Add staged embedding generations|9931f75377e4e0fcd3ac8ceaabf10fc0962a69d8720cfcecd75c03ee7207b51c
            5|Add memory provenance|ac771665d5227db24dd2a047b153c5e84506e076f46980b10c11545e93840430
            6|Add durable lifecycle inbox and retraction lineage|e5ed242cf8bf21d7c1eda5c308223873aad64e1c7e3798b29c54486e3afdca2f
            7|Track embedding configuration fingerprints|9fc22f171b6e88373b93fbce43ee304a070945f3f5a204281c181209b7b3c771
            8|Canonicalize embedding provider identities|3c2d1aebfcffef3672a7e056f07ea0356a01e24dc16486ae03034b91cdf63833
            9|Add resumable embedding generation source fences|83506d5148168f642ad931a6affe3e01de89535bb4d1eae3d17b265b890610b0
            10|Harden migration ledger and secure deleted content|7a14fe58aab5a94519e4676e77b0f214ceb67ea67d63c71eb1c3c82cf832a66f
            11|Enable secure deletion for full-text memory index|47e509ae2183bff545059acb11323b236bc513e8dda268a53ad33144800cb0a0
            """.ReplaceLineEndings("\n"),
            string.Join('\n', actual));
    }

    [Fact]
    public void FullTextSecureDelete_IsPersistentlyEnabled()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();

        Assert.Equal(1, ReadFtsSecureDelete(storage.DatabasePath));
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO SessionMemorySearch(SessionMemorySearch, rank) VALUES('secure-delete', 0);";
            command.ExecuteNonQuery();
        }
        Assert.Equal(0, ReadFtsSecureDelete(storage.DatabasePath));

        _ = storage.OpenStore();
        Assert.Equal(1, ReadFtsSecureDelete(storage.DatabasePath));
    }

    [Fact]
    public void FullTextSecureDeleteMigration_RebuildsAndPhysicallyPurgesLegacyIndex()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        var secret = "legacy-fts-erasure-sentinel-" + Guid.NewGuid().ToString("N");
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA secure_delete = OFF;
                INSERT INTO SessionMemorySearch(SessionMemorySearch, rank) VALUES('secure-delete', 0);
                INSERT INTO SessionMemorySearch(SessionMemorySearch) VALUES('rebuild');
                INSERT INTO SessionMemorySearch (MemoryId, SessionId, Category, Content, EvidenceText, State)
                VALUES ('legacy-fts-row', 'legacy-session', 'project-fact', $secret, $secret, 'Active');
                DELETE FROM SessionMemorySearch WHERE MemoryId = 'legacy-fts-row';
                DELETE FROM MemorySchemaMigrations WHERE Version = 11;
                DELETE FROM SemanticMemoryMaintenance WHERE Name = 'fts5-secure-erase-v11';
                DROP TABLE SemanticDeletionMaintenance;
                """;
            command.Parameters.AddWithValue("$secret", secret);
            command.ExecuteNonQuery();
        }
        Assert.True(DatabaseFilesContain(storage.DatabasePath, secret));

        _ = storage.OpenStore();

        Assert.False(DatabaseFilesContain(storage.DatabasePath, secret));
        Assert.Equal(1, ReadFtsSecureDelete(storage.DatabasePath));
        Assert.Equal(11, Assert.Single(ReadMigrationVersions(storage.DatabasePath).TakeLast(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreLedgerSchemaFixtures_MigrateForwardWithLedgerSearchAndEmbeddings(bool includeLaterLegacyFeatures)
    {
        using var storage = new TemporaryMemoryStorage();
        var sessionId = Guid.NewGuid();
        var memoryId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        CreateLegacyFixture(storage.DatabasePath, sessionId, memoryId, sourceTurnId, includeLaterLegacyFeatures);

        var store = storage.OpenStore();

        var memory = store.GetMemory(memoryId);
        Assert.NotNull(memory);
        Assert.Equal("project-fact", memory.Category);
        Assert.Equal(sourceTurnId, memory.SourceTurnId);
        Assert.Equal(AgentMemoryProvenance.Unknown, memory.Provenance);
        Assert.Single(store.SearchMemories(sessionId, "legacy", null, includeInactive: false, limit: 10));
        Assert.Equal([0.25f, 0.75f], store.GetEmbedding(memoryId)!.Values);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], ReadMigrationVersions(storage.DatabasePath));
    }

    [Fact]
    public void ConcurrentConstructors_ApplyEachMigrationExactlyOnce()
    {
        using var storage = new TemporaryMemoryStorage();

        Parallel.For(0, 12, _ => storage.OpenStore());

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], ReadMigrationVersions(storage.DatabasePath));
    }

    [Fact]
    public void MigrationLedger_BackfillsRecognizedChecksumsAndRejectsDrift()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE LegacyMemorySchemaMigrations AS
                    SELECT Version, Name, AppliedAtUtc
                    FROM MemorySchemaMigrations
                    WHERE Version < 10;
                DROP TABLE MemorySchemaMigrations;
                ALTER TABLE LegacyMemorySchemaMigrations RENAME TO MemorySchemaMigrations;
                """;
            command.ExecuteNonQuery();
        }

        _ = storage.OpenStore();
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM MemorySchemaMigrations WHERE length(Checksum) = 64;";
            Assert.Equal(11, Convert.ToInt32(command.ExecuteScalar()));
            command.CommandText = "UPDATE MemorySchemaMigrations SET Checksum = 'tampered' WHERE Version = 4;";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => storage.OpenStore());
        Assert.Contains("migration ledger validation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not edit or delete migration ledger rows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationLedger_RejectsRenamedRecognizedVersionBeforeChecksumBackfill()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE LegacyMemorySchemaMigrations AS
                    SELECT Version,
                           CASE WHEN Version = 3 THEN 'renamed' ELSE Name END AS Name,
                           AppliedAtUtc
                    FROM MemorySchemaMigrations
                    WHERE Version < 10;
                DROP TABLE MemorySchemaMigrations;
                ALTER TABLE LegacyMemorySchemaMigrations RENAME TO MemorySchemaMigrations;
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => storage.OpenStore());
    }

    [Fact]
    public void FutureMigrationVersion_IsRejected()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO MemorySchemaMigrations (Version, Name, Checksum, AppliedAtUtc) VALUES (12, 'future', 'future', $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => storage.OpenStore());
    }

    [Fact]
    public void ReopenStore_RepairsPartiallyPopulatedFullTextIndex()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var first = AddMemory(store, sessionId, "The project uses a cobalt deployment checklist.");
        var second = AddMemory(store, sessionId, "The project uses a heliotrope release checklist.");
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM SessionMemorySearch WHERE MemoryId = $memoryId;";
            command.Parameters.AddWithValue("$memoryId", second.MemoryId.ToString());
            command.ExecuteNonQuery();
        }

        var reopened = storage.OpenStore();

        Assert.Equal(first.MemoryId, Assert.Single(reopened.SearchMemories(sessionId, "cobalt", null, false, 10)).Memory.MemoryId);
        Assert.Equal(second.MemoryId, Assert.Single(reopened.SearchMemories(sessionId, "heliotrope", null, false, 10)).Memory.MemoryId);
    }

    [Fact]
    public void ReopenStore_RecreatesMissingFullTextIndexWhenLedgerIsCurrent()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var memory = AddMemory(store, sessionId, "The current database survives a missing search table.");
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE SessionMemorySearch;";
            command.ExecuteNonQuery();
        }

        var reopened = storage.OpenStore();

        Assert.Equal(memory.MemoryId, Assert.Single(reopened.SearchMemories(sessionId, "missing", null, false, 10)).Memory.MemoryId);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], ReadMigrationVersions(storage.DatabasePath));
    }

    [Fact]
    public void ProviderIdentityMigration_CanonicalizesPersistedRowsAndCaseInsensitiveLookups()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var memory = AddMemory(store, sessionId, "Provider identity casing is canonicalized.");
        var now = DateTimeOffset.UtcNow;
        store.UpsertEmbedding(new StoredMemoryEmbeddingRecord(
            memory.MemoryId,
            sessionId,
            "provider.one",
            "model-one",
            "hash",
            2,
            [0.25f, 0.75f],
            now,
            now));
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM MemorySchemaMigrations WHERE Version IN (8, 9, 10, 11);
                UPDATE SessionMemoryEmbeddingGenerations SET ProviderId = 'PROVIDER.ONE';
                UPDATE SessionMemoryActiveEmbeddingGenerations SET ProviderId = 'Provider.One';
                UPDATE SessionMemoryEmbeddings SET ProviderId = 'provider.ONE';
                """;
            command.ExecuteNonQuery();
        }

        var migrated = storage.OpenStore();

        var lower = Assert.Single(migrated.ListEmbeddings(sessionId, "provider.one", "model-one")).Value;
        var upper = Assert.Single(migrated.ListEmbeddings(sessionId, "PROVIDER.ONE", "model-one")).Value;
        Assert.Equal("provider.one", lower.ProviderId);
        Assert.Equal(lower.MemoryId, upper.MemoryId);
        Assert.Equal(lower.Values, upper.Values);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], ReadMigrationVersions(storage.DatabasePath));
    }

    [Fact]
    public void ReopenCurrentStore_DoesNotUnconditionallyRebuildHealthyFullTextIndex()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var first = AddMemory(store, sessionId, "First current-schema memory.");
        var second = AddMemory(store, sessionId, "Second current-schema memory.");
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM SessionMemorySearch WHERE MemoryId = $memoryId;
                DELETE FROM SessionMemories WHERE MemoryId = $memoryId;
                """;
            command.Parameters.AddWithValue("$memoryId", first.MemoryId.ToString());
            command.ExecuteNonQuery();
        }

        var rowIdBefore = ReadSearchRowId(storage.DatabasePath, second.MemoryId);
        _ = storage.OpenStore();
        var rowIdAfter = ReadSearchRowId(storage.DatabasePath, second.MemoryId);

        Assert.True(rowIdBefore > 1);
        Assert.Equal(rowIdBefore, rowIdAfter);
    }

    [Fact]
    public void UpdateMemory_CategoryEdit_RoundTripsCanonicalRowAndSearchIndex()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        var created = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "Use the cobalt release checklist.",
            NormalizedContent: "use the cobalt release checklist.",
            EvidenceText: "The checklist was selected for releases.",
            SourceTurnId: sourceTurnId,
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.9f));

        var updated = store.UpdateMemory(
            created.MemoryId,
            category: "standing-instruction",
            content: created.Content,
            note: "Reclassified as a standing instruction.");

        var reopenedStore = storage.OpenStore();
        var canonical = reopenedStore.GetMemory(created.MemoryId);
        Assert.NotNull(canonical);
        var searchResult = Assert.Single(reopenedStore.SearchMemories(
            sessionId,
            "standing",
            preferredCategories: null,
            includeInactive: false,
            limit: 10)).Memory;

        Assert.Equal("standing-instruction", updated.Category);
        Assert.Equal(updated, canonical);
        Assert.Equal(updated, searchResult);
    }

    [Fact]
    public void UpsertMemory_NullSourceTurnId_PreservesPersistedSourceAfterReopen()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "The project codename is heliotrope.",
            NormalizedContent: "the project codename is heliotrope.",
            EvidenceText: "The codename was introduced.",
            SourceTurnId: sourceTurnId,
            IsPinned: false,
            Importance: 0.7f,
            Confidence: 0.8f));

        var updated = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "The project codename is Heliotrope.",
            NormalizedContent: "the project codename is heliotrope.",
            EvidenceText: "The codename was confirmed.",
            SourceTurnId: null,
            IsPinned: true,
            Importance: 0.9f,
            Confidence: 0.95f));

        var reopenedStore = storage.OpenStore();
        var canonical = reopenedStore.GetMemory(updated.MemoryId);
        Assert.NotNull(canonical);
        var searchResult = Assert.Single(reopenedStore.SearchMemories(
            sessionId,
            "heliotrope",
            preferredCategories: null,
            includeInactive: false,
            limit: 10)).Memory;

        Assert.Equal(sourceTurnId, updated.SourceTurnId);
        Assert.Equal(updated, canonical);
        Assert.Equal(updated, searchResult);
    }

    [Fact]
    public void UpsertMemory_TrimsEvidenceCountAndTextLength()
    {
        const int maxEvidenceRecords = 16;
        const int maxEvidenceChars = 4_096;
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        StoredMemoryRecord? memory = null;
        for (var index = 0; index < maxEvidenceRecords + 4; index++)
        {
            memory = store.UpsertMemory(new MemoryUpsertRequest(
                sessionId,
                "project-fact",
                "The project has bounded evidence.",
                "the project has bounded evidence.",
                $"{index:D2}:" + new string('x', maxEvidenceChars + 100),
                Guid.NewGuid(),
                false,
                0.8f,
                0.9f,
                AgentMemoryProvenance.User));
        }

        var evidence = store.ListEvidence(memory!.MemoryId);
        Assert.Equal(maxEvidenceRecords, evidence.Count);
        Assert.All(evidence, item => Assert.InRange(item.EvidenceText!.Length, 1, maxEvidenceChars));
    }

    [Fact]
    public async Task ConcurrentSessionDeletionAndMutation_NeverResurrectsMemory()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var memory = AddMemory(store, sessionId, "A deletion race must not resurrect this memory.");
        using var start = new Barrier(2);

        var mutation = Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                store.UpdateMemory(memory.MemoryId, memory.Category, "A racing replacement.", "Race test.");
            }
            catch (InvalidOperationException)
            {
                // Deletion won the immediate-transaction race.
            }
        });
        var deletion = Task.Run(() =>
        {
            start.SignalAndWait();
            store.DeleteSessionData(sessionId);
        });

        await Task.WhenAll(mutation, deletion);

        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));
        Assert.Throws<InvalidOperationException>(() => AddMemory(store, sessionId, "Late resurrection attempt."));
    }

    [Fact]
    public void SessionDeletion_SecurelyErasesContentAndTruncatesWal()
    {
        using var storage = new TemporaryMemoryStorage();
        var store = storage.OpenStore();
        var sessionId = Guid.NewGuid();
        var secret = "physical-erasure-sentinel-" + Guid.NewGuid().ToString("N");
        AddMemory(store, sessionId, secret);
        Assert.True(DatabaseFilesContain(storage.DatabasePath, secret));

        store.DeleteSessionData(sessionId);

        Assert.Empty(store.ListMemories(sessionId, includeInactive: true));
        Assert.False(DatabaseFilesContain(storage.DatabasePath, secret));
        Assert.False(File.Exists(storage.DatabasePath + "-wal")
                     && new FileInfo(storage.DatabasePath + "-wal").Length > 0);
    }

    private sealed class TemporaryMemoryStorage : IDisposable
    {
        private readonly string _rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-memory-persistence-tests",
            Guid.NewGuid().ToString("N"));
        private readonly TestPackageContext _context;

        public TemporaryMemoryStorage()
        {
            _context = new TestPackageContext(_rootPath);
        }

        public MemoryLocalStore OpenStore() => new(_context);

        public string DatabasePath => _context.Storage.RoleLocalWorkspace.GetLocalPath("memory/agent-memory.db");

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private static StoredMemoryRecord AddMemory(MemoryLocalStore store, Guid sessionId, string content)
        => store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            "project-fact",
            content,
            content.ToLowerInvariant(),
            "Fixture evidence.",
            Guid.NewGuid(),
            false,
            0.8f,
            0.9f));

    private static void CreateLegacyFixture(
        string databasePath,
        Guid sessionId,
        Guid memoryId,
        Guid sourceTurnId,
        bool includeLaterLegacyFeatures)
    {
        SQLitePCL.Batteries_V2.Init();
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("O");
        command.CommandText = """
            CREATE TABLE SessionMemories (
                MemoryId TEXT PRIMARY KEY, SessionId TEXT NOT NULL, Category TEXT NOT NULL, Content TEXT NOT NULL,
                NormalizedContent TEXT NOT NULL, EvidenceText TEXT NULL, SourceTurnId TEXT NULL, Importance REAL NOT NULL,
                Confidence REAL NOT NULL, IsPinned INTEGER NOT NULL DEFAULT 0, State TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, LastAccessedAtUtc TEXT NULL,
                AccessCount INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE SessionMemoryEvidence (
                EvidenceId TEXT PRIMARY KEY, MemoryId TEXT NOT NULL, SessionId TEXT NOT NULL, SourceTurnId TEXT NULL,
                EvidenceText TEXT NULL, CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE SessionMemoryEmbeddings (
                MemoryId TEXT PRIMARY KEY, SessionId TEXT NOT NULL, ProviderId TEXT NOT NULL, ModelId TEXT NOT NULL,
                CanonicalTextHash TEXT NOT NULL, Dimensions INTEGER NOT NULL, VectorJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL
            );
            INSERT INTO SessionMemories
                (MemoryId, SessionId, Category, Content, NormalizedContent, EvidenceText, SourceTurnId, Importance,
                 Confidence, IsPinned, State, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc, AccessCount)
            VALUES ($memoryId, $sessionId, 'project-fact', 'Legacy project memory.', 'legacy project memory.',
                'Legacy evidence.', $sourceTurnId, 0.8, 0.9, 0, 'Active', $now, $now, NULL, 0);
            INSERT INTO SessionMemoryEmbeddings
                (MemoryId, SessionId, ProviderId, ModelId, CanonicalTextHash, Dimensions, VectorJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($memoryId, $sessionId, 'fixture-provider', 'fixture-model', 'legacy-hash', 2, '[0.25,0.75]', $now, $now);
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$sourceTurnId", sourceTurnId.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        if (includeLaterLegacyFeatures)
        {
            using var laterSchema = connection.CreateCommand();
            laterSchema.CommandText = """
                ALTER TABLE SessionMemories ADD COLUMN SupersededByMemoryId TEXT NULL;
                CREATE VIRTUAL TABLE SessionMemorySearch USING fts5(
                    MemoryId UNINDEXED, SessionId UNINDEXED, Category, Content, EvidenceText, State UNINDEXED
                );
                INSERT INTO SessionMemorySearch (MemoryId, SessionId, Category, Content, EvidenceText, State)
                VALUES ('stale-row', $sessionId, 'stale', 'Stale search row.', NULL, 'Active');
                """;
            laterSchema.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            laterSchema.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<int> ReadMigrationVersions(string databasePath)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM MemorySchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var versions = new List<int>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static int ReadFtsSecureDelete(string databasePath)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM SessionMemorySearch_config WHERE k = 'secure-delete';";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static long ReadSearchRowId(string databasePath, Guid memoryId)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM SessionMemorySearch WHERE MemoryId = $memoryId;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool DatabaseFilesContain(string databasePath, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new[] { databasePath, databasePath + "-wal" }
            .Where(File.Exists)
            .Any(path => File.ReadAllBytes(path).AsSpan().IndexOf(bytes) >= 0);
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => "test.package.agent.memory.semantic";

        public string Version => "1.0.0";

        public string ContentRootPath => rootPath;

        public IPackageStorageContext Storage { get; } = new TestPackageStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestPackageSettings();

        public IPackageSecrets Secrets { get; } = new TestPackageSecrets();


        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public TestPackageStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new TestPackageFileStore(rootPath);
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; } = new TestPackageKeyValueStore();

        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class TestPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class TestPackageKeyValueStore : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult<string?>(null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(false);
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class TestPackageSettings : EmptyPackageSettings;

    private sealed class TestPackageSecrets : InMemoryPackageSecrets;
}

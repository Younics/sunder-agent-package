using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class MemoryLocalStorePersistenceTests
{
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
        Assert.Single(store.SearchMemories(sessionId, "legacy", null, includeInactive: false, limit: 10));
        Assert.Equal([0.25f, 0.75f], store.GetEmbedding(memoryId)!.Values);
        Assert.Equal([1, 2, 3, 4], ReadMigrationVersions(storage.DatabasePath));
    }

    [Fact]
    public void ConcurrentConstructors_ApplyEachMigrationExactlyOnce()
    {
        using var storage = new TemporaryMemoryStorage();

        Parallel.For(0, 12, _ => storage.OpenStore());

        Assert.Equal([1, 2, 3, 4], ReadMigrationVersions(storage.DatabasePath));
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
        Assert.Equal([1, 2, 3, 4], ReadMigrationVersions(storage.DatabasePath));
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

    private static long ReadSearchRowId(string databasePath, Guid memoryId)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM SessionMemorySearch WHERE MemoryId = $memoryId;";
        command.Parameters.AddWithValue("$memoryId", memoryId.ToString());
        return Convert.ToInt64(command.ExecuteScalar());
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

        public string InstallPath => rootPath;

        public IPackageStorageContext Storage { get; } = new TestPackageStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestPackageSettings();

        public IPackageSecrets Secrets { get; } = new TestPackageSecrets();

        public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

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
            => Task.FromResult<string?>(null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestPackageSettings : EmptyPackageSettings;

    private sealed class TestPackageSecrets : InMemoryPackageSecrets;
}

extern alias MemoryWorker;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using MemoryWorker::Sunder.Package.Agent.Contracts.Models;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class MemoryLocalStorePersistenceTests
{
    [Fact]
    public void FreshStore_CreatesExactCurrentBaseline()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        using var connection = OpenConnection(storage.DatabasePath);
        Assert.Equal(
            $"1|{MemorySchemaMigrator.BaselineName}|{MemorySchemaMigrator.BaselineChecksum}",
            ExecuteString(connection, "SELECT Version || '|' || Name || '|' || Checksum FROM MemorySchemaMigrations;"));
        Assert.Equal("ed3e1696f550d4f3b16bf440806d60fbfc8a90d7004c5605621f4963bbbb2de1", MemorySchemaMigrator.BaselineChecksum);
        Assert.Equal(
            MemorySchemaMigrator.BaselineChecksum,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MemorySchemaMigrator.BaselineSql))).ToLowerInvariant());
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM MemorySchemaMigrations;"));
        Assert.Equal("wal", ExecuteString(connection, "PRAGMA journal_mode;"));
        using var operationalConnection = MemoryDatabase.OpenConnection(storage.DatabasePath);
        Assert.Equal(1L, ExecuteInt64(operationalConnection, "PRAGMA secure_delete;"));
        Assert.Equal(1, ReadFtsSecureDelete(storage.DatabasePath));
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
    public void FullTextSecureDeleteMaintenance_ReenablesCurrentIndex()
    {
        using var storage = new TemporaryMemoryStorage();
        _ = storage.OpenStore();
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO SessionMemorySearch(SessionMemorySearch, rank) VALUES('secure-delete', 0);";
            command.ExecuteNonQuery();
        }

        _ = storage.OpenStore();

        Assert.Equal(1, ReadFtsSecureDelete(storage.DatabasePath));
    }

    [Theory]
    [InlineData("ledgerless")]
    [InlineData("partial")]
    [InlineData("old-ledger")]
    [InlineData("tampered")]
    public void UnsupportedDatabase_IsRejectedWithoutMutation(string shape)
    {
        using var storage = new TemporaryMemoryStorage();
        using (var connection = OpenConnection(storage.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = shape switch
            {
                "ledgerless" => "CREATE TABLE ExistingData (Value TEXT); INSERT INTO ExistingData VALUES ('keep');",
                "partial" => "CREATE TABLE MemorySchemaMigrations (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL);",
                "old-ledger" => """
                    CREATE TABLE MemorySchemaMigrations (
                        Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, Checksum TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
                    INSERT INTO MemorySchemaMigrations VALUES (11, 'Enable secure deletion for full-text memory index', 'old', '2026-01-01T00:00:00Z');
                    """,
                "tampered" => """
                    CREATE TABLE MemorySchemaMigrations (
                        Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, Checksum TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
                    INSERT INTO MemorySchemaMigrations VALUES (1, 'semantic-memory-2.0-baseline', 'tampered', '2026-01-01T00:00:00Z');
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(storage.DatabasePath);

        var exception = Assert.Throws<InvalidOperationException>(() => storage.OpenStore());

        Assert.Contains("migration ledger validation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(storage.DatabasePath));
    }

    [Fact]
    public void ConcurrentConstructors_CreateOneBaselineEntry()
    {
        using var storage = new TemporaryMemoryStorage();

        Parallel.For(0, 12, _ => storage.OpenStore());

        using var connection = OpenConnection(storage.DatabasePath);
        Assert.Equal(1L, ExecuteInt64(connection, "SELECT COUNT(*) FROM MemorySchemaMigrations;"));
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

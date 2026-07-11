using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

public sealed class SkillImportReplacementTests
{
    [Fact]
    public void Store_MalformedIndexCannotBeOverwrittenAsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(root);
            var indexPath = context.Storage.LocalWorkspace.GetLocalPath("skills/skills.json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            const string malformedIndex = "{not-json";
            File.WriteAllText(indexPath, malformedIndex);
            var store = new SkillStore(context);

            Assert.Throws<InvalidDataException>(() => store.ListSkills());
            Assert.Throws<InvalidDataException>(() => store.SaveSkill(CreateRecord("new-skill")));
            Assert.Equal(malformedIndex, File.ReadAllText(indexPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Store_TransientReadFailureCannotBeOverwrittenAsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(root);
            var store = new SkillStore(context);
            store.SaveSkill(CreateRecord("existing"));
            var indexPath = context.Storage.LocalWorkspace.GetLocalPath("skills/skills.json");
            var originalIndex = File.ReadAllBytes(indexPath);

            using (new FileStream(indexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => store.SaveSkill(CreateRecord("new-skill")));
            }

            Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
            Assert.Equal("existing", Assert.Single(store.ListSkills()).SkillId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Store_ConcurrentInstancesDoNotLoseUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(root);
            const int saveCount = 24;
            var stores = Enumerable.Range(0, saveCount).Select(_ => new SkillStore(context)).ToArray();

            await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
                store.SaveSkill(CreateRecord($"skill-{index:00}")))));

            Assert.Equal(saveCount, new SkillStore(context).ListSkills().Count);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Store_WaitsForExclusivePersistenceLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(root);
            var store = new SkillStore(context);
            var lockPath = context.Storage.LocalWorkspace.GetLocalPath("skills/skills.json.lock");
            using var externalLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var save = Task.Run(() => store.SaveSkill(CreateRecord("blocked")));
            await Task.Delay(100);
            Assert.False(save.IsCompleted);

            externalLease.Dispose();
            await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("blocked", Assert.Single(store.ListSkills()).SkillId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public Task ImportLocalFolderAsync_FailureAfterBackup_RestoresPriorContentAndIndex()
        => AssertReplacementFailureRestoresPriorStateAsync(SkillImportFaultPoint.AfterBackup);

    [Fact]
    public Task ImportLocalFolderAsync_FailureAfterStagedMove_RestoresPriorContentAndIndex()
        => AssertReplacementFailureRestoresPriorStateAsync(SkillImportFaultPoint.AfterStagedMove);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentImports_SerializeFilesystemAndIndexForSameAndDifferentIds(bool sameId)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-interleaving-tests", Guid.NewGuid().ToString("N"));
        using var releaseFirst = new ManualResetEventSlim();
        try
        {
            var firstId = "interleaved-first";
            var secondId = sameId ? firstId : "interleaved-second";
            var firstSource = CreateSkillSource(root, "first", "1.0.0", "first content", firstId);
            var secondSource = CreateSkillSource(root, "second", "2.0.0", "second content", secondId);
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var firstStore = new SkillStore(context);
            var secondStore = new SkillStore(context);
            var firstReachedSwap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReachedSwap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstImporter = new SkillImportService(firstStore, new UnusedGitHubSkillClient(), context, point =>
            {
                if (point == SkillImportFaultPoint.AfterStagedMove)
                {
                    firstReachedSwap.TrySetResult();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5));
                }
            });
            var secondImporter = new SkillImportService(secondStore, new UnusedGitHubSkillClient(), context, point =>
            {
                if (point == SkillImportFaultPoint.AfterStagedMove)
                {
                    secondReachedSwap.TrySetResult();
                }
            });

            var firstImport = Task.Run(() => firstImporter.ImportLocalFolderAsync(firstSource));
            await firstReachedSwap.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondImport = Task.Run(() => secondImporter.ImportLocalFolderAsync(secondSource));
            await Task.Delay(100);

            Assert.False(secondReachedSwap.Task.IsCompleted);
            releaseFirst.Set();
            await Task.WhenAll(firstImport, secondImport).WaitAsync(TimeSpan.FromSeconds(5));

            var records = new SkillStore(context).ListSkills();
            Assert.Equal(sameId ? 1 : 2, records.Count);
            var second = Assert.IsType<InstalledSkillRecord>(new SkillStore(context).GetSkill(secondId));
            Assert.Equal("2.0.0", second.Version);
            Assert.Equal("second content", await File.ReadAllTextAsync(Path.Combine(secondStore.GetSkillRootPath(second), "resource.txt")));
            if (!sameId)
            {
                Assert.NotNull(new SkillStore(context).GetSkill(firstId));
            }
        }
        finally
        {
            releaseFirst.Set();
            TryDeleteDirectory(root);
        }
    }

    private static async Task AssertReplacementFailureRestoresPriorStateAsync(SkillImportFaultPoint faultPoint)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-replacement-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var originalSource = CreateSkillSource(root, "original", "1.0.0", "original content");
            var replacementSource = CreateSkillSource(root, "replacement", "2.0.0", "replacement content");
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var originalRecord = await new SkillImportService(store, new UnusedGitHubSkillClient(), context)
                .ImportLocalFolderAsync(originalSource);
            var originalIndex = await File.ReadAllBytesAsync(context.Storage.LocalWorkspace.GetLocalPath("skills/skills.json"));
            var importer = new SkillImportService(
                store,
                new UnusedGitHubSkillClient(),
                context,
                point =>
                {
                    if (point == faultPoint)
                    {
                        throw new IOException($"Injected failure at {point}.");
                    }
                });

            var error = await Assert.ThrowsAsync<IOException>(() => importer.ImportLocalFolderAsync(replacementSource));

            Assert.Contains(faultPoint.ToString(), error.Message, StringComparison.Ordinal);
            var restoredRecord = Assert.IsType<InstalledSkillRecord>(store.GetSkill("replacement-safety"));
            Assert.Equal(originalRecord.ContentHash, restoredRecord.ContentHash);
            Assert.Equal(originalRecord.Version, restoredRecord.Version);
            Assert.Equal(originalRecord.UpdatedAtUtc, restoredRecord.UpdatedAtUtc);
            Assert.Equal("original content", await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(restoredRecord), "resource.txt")));
            Assert.Equal(originalIndex, await File.ReadAllBytesAsync(context.Storage.LocalWorkspace.GetLocalPath("skills/skills.json")));
            Assert.Empty(Directory.EnumerateDirectories(store.SkillsRootPath, "replacement-safety.backup-*"));
            Assert.Empty(Directory.EnumerateDirectories(context.Storage.LocalWorkspace.GetLocalPath("skill-import"), "*"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateSkillSource(
        string root,
        string folderName,
        string version,
        string resourceContent,
        string skillId = "replacement-safety")
    {
        var source = Path.Combine(root, folderName);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), $$"""
            ---
            name: {{skillId}}
            description: Replacement safety test.
            version: {{version}}
            ---
            # Replacement Safety
            """);
        File.WriteAllText(Path.Combine(source, "resource.txt"), resourceContent);
        return source;
    }

    private static InstalledSkillRecord CreateRecord(string skillId)
    {
        var now = DateTimeOffset.UtcNow;
        return new InstalledSkillRecord(
            skillId,
            $"skills/{skillId}",
            skillId,
            "Test skill",
            "1.0.0",
            null,
            "local",
            null,
            null,
            null,
            "hash",
            now,
            now,
            new Dictionary<string, string>(),
            []);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup should not hide assertion failures.
        }
    }

    private sealed class UnusedGitHubSkillClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => "sunder.package.agent.skills";

        public string Version { get; } = "1.0.0";

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageConfiguration Configuration { get; } = new TestConfiguration();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string rootPath)
        {
            Files = new TestFileStore();
            LocalWorkspace = new TestWorkspace(rootPath);
            Directory.CreateDirectory(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();

        public IPackageLocalWorkspaceLease LocalWorkspace { get; }
    }

    private sealed class TestFileStore : IPackageFileStore
    {
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestConfiguration : IPackageConfiguration
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class TestSecrets : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestWorkspace(string rootPath) : IPackageLocalWorkspaceLease
    {
        public string WorkspaceRootPath { get; } = rootPath;
        public string GetLocalPath(string relativePath) => Path.Combine(WorkspaceRootPath, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

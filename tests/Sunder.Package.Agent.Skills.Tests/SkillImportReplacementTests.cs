using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using System.Text.Json;
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
            var indexPath = context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json");
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
            var indexPath = context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json");
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
            var lockPath = context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json.lock");
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

    [Theory]
    [InlineData("case-collision")]
    [InlineData("path-depth")]
    [InlineData("file-count")]
    [InlineData("per-file-size")]
    [InlineData("aggregate-size")]
    public async Task GitHubAcquisition_RejectsAdversarialTreesBeforeDownloading(string adversary)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-adversarial-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(root);
            var files = CreateAdversarialGitHubFiles(adversary);
            var client = new BoundedGitHubSkillClient(files);
            var acquirer = new SkillSourceAcquirer(client, context);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => acquirer.AcquireGitHubAsync(
                new GitHubSkillFolder("owner", "repo", "main", "skill", "commit", "tree"),
                CancellationToken.None));

            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.Equal(0, client.ReadCount);
            var stagingParent = context.Storage.RoleLocalWorkspace.GetLocalPath("skill-import");
            Assert.False(Directory.Exists(stagingParent) && Directory.EnumerateDirectories(stagingParent).Any());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("symlink")]
    [InlineData("cancellation")]
    public async Task Replacement_PreflightFailurePreservesPriorSkill(string adversary)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-preflight-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var originalSource = CreateSkillSource(root, "original", "1.0.0", "original content");
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new UnusedGitHubSkillClient(), context);
            var original = await importer.ImportLocalFolderAsync(originalSource);
            var replacement = CreateSkillSource(root, "replacement", "2.0.0", "replacement content");
            using var cancellation = new CancellationTokenSource();
            if (adversary == "invalid-utf8")
            {
                await File.WriteAllBytesAsync(Path.Combine(replacement, "SKILL.md"), [0xff, 0xfe, 0xfd]);
            }
            else if (adversary == "symlink")
            {
                File.CreateSymbolicLink(Path.Combine(replacement, "linked.txt"), Path.Combine(originalSource, "resource.txt"));
            }
            else
            {
                cancellation.Cancel();
            }

            await Assert.ThrowsAnyAsync<Exception>(() => importer.ImportLocalFolderAsync(replacement, cancellation.Token));

            var restored = Assert.IsType<InstalledSkillRecord>(store.GetSkill(original.SkillId));
            Assert.Equal(original.ContentHash, restored.ContentHash);
            Assert.Equal("original content", await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(restored), "resource.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GitHubImport_DownloadsResolvedCommitWhenBranchMoves()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-branch-move-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(root);
            var client = new BranchMovingGitHubSkillClient();
            var imported = await new SkillImportService(new SkillStore(context), client, context)
                .ImportGitHubFolderAsync("https://github.com/owner/repo/tree/main/skill");

            Assert.Equal("fixed-commit", imported.ResolvedCommitSha);
            Assert.All(client.ReadCommitShas, sha => Assert.Equal("fixed-commit", sha));
            Assert.Equal("from fixed commit", await File.ReadAllTextAsync(Path.Combine(new SkillStore(context).GetSkillRootPath(imported), "resource.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MultiSkillReplacement_FaultRollsBackEntireBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-batch-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var importer = new SkillImportService(store, new UnusedGitHubSkillClient(), context);
            await importer.ImportLocalFolderAsync(CreateSkillSource(root, "old-one", "1.0.0", "old one", "one"));
            await importer.ImportLocalFolderAsync(CreateSkillSource(root, "old-two", "1.0.0", "old two", "two"));
            var batch = Path.Combine(root, "batch");
            CreateSkillSource(batch, "new-one", "2.0.0", "new one", "one");
            CreateSkillSource(batch, "new-two", "2.0.0", "new two", "two");
            var stagedMoves = 0;
            var faulting = new SkillImportService(store, new UnusedGitHubSkillClient(), context, point =>
            {
                if (point == SkillImportFaultPoint.AfterStagedMove && ++stagedMoves == 2)
                {
                    throw new IOException("Injected second-skill interruption.");
                }
            });

            await Assert.ThrowsAsync<IOException>(() => faulting.ImportLocalSkillsAsync(batch));

            Assert.Equal("old one", await ReadResourceAsync(store, "one"));
            Assert.Equal("old two", await ReadResourceAsync(store, "two"));
            Assert.All(store.ListSkills(), record => Assert.Equal("1.0.0", record.Version));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StoreStartup_RecoversPreparedFilesystemAndIndexDivergence()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var context = new TestPackageContext(Path.Combine(root, "install"));
            var store = new SkillStore(context);
            var original = await new SkillImportService(store, new UnusedGitHubSkillClient(), context)
                .ImportLocalFolderAsync(CreateSkillSource(root, "original", "1.0.0", "original", "recover"));
            var snapshot = store.CaptureIndexSnapshot();
            var target = store.GetSkillRootPath(original);
            var backup = target + ".backup-crash";
            var staging = context.Storage.RoleLocalWorkspace.GetLocalPath("skill-import/crash");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "SKILL.md"), "---\nname: recover\nversion: 2.0.0\n---\n# Recover\n");
            File.WriteAllText(Path.Combine(staging, "resource.txt"), "replacement");
            Directory.Move(target, backup);
            Directory.Move(staging, target);
            var journal = new SkillImportJournal(
                "Prepared",
                Convert.ToBase64String(snapshot!),
                [new SkillImportJournalEntry(staging, target, backup, true, true, true)]);
            File.WriteAllBytes(store.ImportJournalPath, JsonSerializer.SerializeToUtf8Bytes(journal));

            var recoveredStore = new SkillStore(context);

            Assert.Equal("original", await ReadResourceAsync(recoveredStore, "recover"));
            Assert.Equal("1.0.0", recoveredStore.GetSkill("recover")?.Version);
            Assert.False(File.Exists(recoveredStore.ImportJournalPath));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SkillImportLimits_AreSecurityRatchets()
    {
        Assert.Equal(2_048, SkillSourceAcquirer.Limits.MaxFiles);
        Assert.Equal(16, SkillSourceAcquirer.Limits.MaxPathDepth);
        Assert.Equal(10 * 1024 * 1024, SkillSourceAcquirer.Limits.MaxFileBytes);
        Assert.Equal(50 * 1024 * 1024, SkillSourceAcquirer.Limits.MaxTotalBytes);
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
            var originalIndex = await File.ReadAllBytesAsync(context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json"));
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
            Assert.Equal(originalIndex, await File.ReadAllBytesAsync(context.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json")));
            Assert.Empty(Directory.EnumerateDirectories(store.SkillsRootPath, "replacement-safety.backup-*"));
            Assert.Empty(Directory.EnumerateDirectories(context.Storage.RoleLocalWorkspace.GetLocalPath("skill-import"), "*"));
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

    private static async Task<string> ReadResourceAsync(SkillStore store, string skillId)
    {
        var record = Assert.IsType<InstalledSkillRecord>(store.GetSkill(skillId));
        return await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(record), "resource.txt"));
    }

    private static IReadOnlyList<GitHubSkillFile> CreateAdversarialGitHubFiles(string adversary)
    {
        var files = new List<GitHubSkillFile>
        {
            new("SKILL.md", "skill/SKILL.md", 10),
        };
        switch (adversary)
        {
            case "case-collision":
                files.Add(new("docs/readme.md", "skill/docs/readme.md", 1));
                files.Add(new("DOCS/README.md", "skill/DOCS/README.md", 1));
                break;
            case "path-depth":
                var deepPath = string.Join('/', Enumerable.Repeat("level", 17)) + "/file.txt";
                files.Add(new(deepPath, "skill/" + deepPath, 1));
                break;
            case "file-count":
                files.AddRange(Enumerable.Range(0, 2_048)
                    .Select(index => new GitHubSkillFile($"file-{index}.txt", $"skill/file-{index}.txt", 1)));
                break;
            case "per-file-size":
                files.Add(new("large.bin", "skill/large.bin", 10L * 1024 * 1024 + 1));
                break;
            case "aggregate-size":
                files.AddRange(Enumerable.Range(0, 6)
                    .Select(index => new GitHubSkillFile($"large-{index}.bin", $"skill/large-{index}.bin", 10L * 1024 * 1024)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(adversary));
        }

        return files;
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

    private sealed class BoundedGitHubSkillClient(IReadOnlyList<GitHubSkillFile> files) : IGitHubSkillClient
    {
        public int ReadCount { get; private set; }

        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default)
            => Task.FromResult(files);

        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult("---\nname: test\n---\n"u8.ToArray());
        }
    }

    private sealed class BranchMovingGitHubSkillClient : IGitHubSkillClient
    {
        public List<string> ReadCommitShas { get; } = [];

        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("main");

        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(null);

        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GitHubSkillFolder?>(
                request.Ref == "main" && request.FolderPath == "skill"
                    ? new GitHubSkillFolder(request.Owner, request.Repo, request.Ref, request.FolderPath, "fixed-commit", "fixed-tree")
                    : null);

        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GitHubSkillFile>>(
                [new("SKILL.md", "skill/SKILL.md", 64), new("resource.txt", "skill/resource.txt", 17)]);

        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default)
        {
            ReadCommitShas.Add(folder.CommitSha);
            return Task.FromResult(file.RelativePath == "SKILL.md"
                ? "---\nname: branch-safe\nversion: 1.0.0\n---\n# Branch Safe\n"u8.ToArray()
                : "from fixed commit"u8.ToArray());
        }
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => "sunder.package.agent.skills";

        public string Version { get; } = "1.0.0";

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestSettings();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string rootPath)
        {
            Files = new TestFileStore();
            RoleLocalWorkspace = new TestWorkspace(rootPath);
            Directory.CreateDirectory(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();

        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
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

    private sealed class TestSettings : IPackageSettings
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestSecrets : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestWorkspace(string rootPath) : IPackageRoleLocalWorkspace
    {
        public string WorkspaceRootPath { get; } = rootPath;
        public string GetLocalPath(string relativePath) => Path.Combine(WorkspaceRootPath, relativePath);
    }
}

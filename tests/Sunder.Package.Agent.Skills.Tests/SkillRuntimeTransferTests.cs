extern alias SkillsApp;

using AppRuntime = SkillsApp::Sunder.Package.Agent.Skills.Runtime;
using SkillAppRuntimeGateway = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillAppRuntimeGateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Package.Agent.Skills.Tests;

public sealed class SkillRuntimeTransferTests
{
    [Fact]
    public async Task LocalFolderImport_TransfersBoundedContentWithoutSendingAppPathToRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-transfer-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "app-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), "---\nname: Transfer Test\n---\nUse transfer test.");
        await File.WriteAllTextAsync(Path.Combine(source, "resource.txt"), "runtime-readable content");
        try
        {
            var files = new MemoryFileStore();
            var runtimeContext = new TestContext(Path.Combine(root, "runtime"), files);
            var store = new SkillStore(runtimeContext);
            var importer = new SkillImportService(store, new UnusedGitHubClient(), runtimeContext);
            var handler = new SkillRuntimeHandler(store, importer, runtimeContext);
            var client = new HandlerRuntimeClient(handler);
            var appContext = new TestContext(Path.Combine(root, "app"), files);
            using var gateway = new SkillAppRuntimeGateway(client, appContext);

            var imported = await gateway.ImportLocalAsync(source);

            var skill = Assert.Single(imported);
            Assert.Null(skill.SourceUri);
            var stored = Assert.Single(store.ListSkills());
            Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(skill));
            Assert.Equal("runtime-readable content", await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(stored), "resource.txt")));
            Assert.NotNull(client.LastCommand);
            Assert.Equal(SkillCommandKind.ImportTransfer, client.LastCommand!.Kind);
            Assert.NotEqual(source, client.LastCommand.Value);
            Assert.Matches("^[0-9a-f]{32}$", client.LastCommand.Value!);
            Assert.Empty(files.Paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("symlink")]
    [InlineData("alternate-data-stream")]
    public async Task RuntimeTransfer_RejectsUnsafeOrOversizedArchiveBeforeImport(string adversary)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-transfer-adversarial-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new MemoryFileStore();
            var runtimeRoot = Path.Combine(root, "runtime");
            var runtimeContext = new TestContext(runtimeRoot, files);
            var store = new SkillStore(runtimeContext);
            var importer = new SkillImportService(store, new UnusedGitHubClient(), runtimeContext);
            var handler = new SkillRuntimeHandler(store, importer, runtimeContext);
            var transferId = Guid.NewGuid().ToString("N");
            var archive = CreateAdversarialArchive(adversary);
            var transferRoot = $"skill-import-transfers/{transferId}";
            await files.WriteAsync($"{transferRoot}/chunks/0000.bin", archive);
            await files.WriteAsync(
                $"{transferRoot}/manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(new SkillTransferManifest(
                    1,
                    archive.LongLength,
                    Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant())));

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new SkillCommand(SkillCommandKind.ImportTransfer, transferId))
                    .AsTask());

            Assert.Empty(store.ListSkills());
            Assert.Empty(files.Paths);
            Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "skill-transfer", transferId)));
            Assert.False(File.Exists(Path.Combine(runtimeRoot, "skill-transfer", transferId + ".zip")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"ChunkCount\":1,\"TotalBytes\":1,\"Sha256\":null}")]
    public async Task RuntimeTransfer_RejectsMalformedManifestAndCleansUploads(string manifestJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-transfer-manifest-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new MemoryFileStore();
            var runtimeContext = new TestContext(root, files);
            var handler = new SkillRuntimeHandler(
                new SkillStore(runtimeContext),
                new SkillImportService(new SkillStore(runtimeContext), new UnusedGitHubClient(), runtimeContext),
                runtimeContext);
            var transferId = Guid.NewGuid().ToString("N");
            var transferRoot = $"skill-import-transfers/{transferId}";
            await files.WriteAsync($"{transferRoot}/chunks/0000.bin", new byte[] { 1 });
            await files.WriteAsync($"{transferRoot}/manifest.json", System.Text.Encoding.UTF8.GetBytes(manifestJson));

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                handler.HandleAsync(new SkillCommand(SkillCommandKind.ImportTransfer, transferId)).AsTask());

            Assert.Empty(files.Paths);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppTransfer_AmbiguousChunkWriteIsStillCleanedUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-transfer-cleanup-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), "---\nname: Cleanup Test\n---\n# Cleanup");
        try
        {
            var files = new MemoryFileStore { FailAfterWriteCall = 1 };
            var runtimeContext = new TestContext(Path.Combine(root, "runtime"), files);
            var handler = new SkillRuntimeHandler(
                new SkillStore(runtimeContext),
                new SkillImportService(new SkillStore(runtimeContext), new UnusedGitHubClient(), runtimeContext),
                runtimeContext);
            using var gateway = new SkillAppRuntimeGateway(
                new HandlerRuntimeClient(handler),
                new TestContext(Path.Combine(root, "app"), files));

            await Assert.ThrowsAsync<IOException>(() => gateway.ImportLocalAsync(source));

            Assert.Empty(files.Paths);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppStartup_ReplaysOrphanedTransferCleanupMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-transfer-orphan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var transferId = Guid.NewGuid().ToString("N");
            var transferRoot = $"skill-import-transfers/{transferId}";
            var files = new MemoryFileStore();
            var state = new MemoryStateStore();
            var appContext = new TestContext(Path.Combine(root, "app"), files, state);
            await files.WriteAsync($"{transferRoot}/chunks/0000.bin", new byte[] { 1, 2, 3 });
            await files.WriteAsync($"{transferRoot}/manifest.json", new byte[] { 4, 5, 6 });
            await state.SetValueAsync(SkillAppRuntimeGateway.TransferCleanupKey(transferId), "pending");
            var archive = appContext.Storage.RoleLocalWorkspace.GetLocalPath($"skill-transfer/{transferId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            await File.WriteAllBytesAsync(archive, new byte[] { 7, 8, 9 });

            using var gateway = new SkillAppRuntimeGateway(
                new HandlerRuntimeClient(new SkillRuntimeHandler(
                    new SkillStore(appContext),
                    new SkillImportService(new SkillStore(appContext), new UnusedGitHubClient(), appContext),
                    appContext)),
                appContext);
            await gateway.PendingTransferCleanup.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Empty(files.Paths);
            Assert.False(File.Exists(archive));
            Assert.False(await state.ContainsKeyAsync(SkillAppRuntimeGateway.TransferCleanupKey(transferId)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RuntimeStartupCleansOrphanedTransferStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-skill-runtime-orphan-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var transferId = Guid.NewGuid().ToString("N");
            var stagingParent = Path.Combine(root, "skill-transfer");
            var staging = Path.Combine(stagingParent, transferId);
            var archive = Path.Combine(stagingParent, transferId + ".zip");
            var unrelated = Path.Combine(stagingParent, "keep.txt");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "partial.txt"), "partial");
            await File.WriteAllTextAsync(archive, "partial archive");
            await File.WriteAllTextAsync(unrelated, "unrelated");
            var context = new TestContext(root, new MemoryFileStore());
            var store = new SkillStore(context);

            var handler = new SkillRuntimeHandler(
                store,
                new SkillImportService(store, new UnusedGitHubClient(), context),
                context);
            await handler.OrphanedTransferCleanup.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(Directory.Exists(staging));
            Assert.False(File.Exists(archive));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateAdversarialArchive(string adversary)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(adversary switch
            {
                "symlink" => "linked",
                "alternate-data-stream" => "resource.txt:payload",
                _ => "large.bin",
            });
            if (adversary == "symlink")
            {
                entry.ExternalAttributes = unchecked((int)(0xa000u << 16));
                using var writer = new StreamWriter(entry.Open());
                writer.Write("outside");
            }
            else if (adversary == "alternate-data-stream")
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write("hidden payload");
            }
            else
            {
                using var stream = entry.Open();
                var block = new byte[64 * 1024];
                var remaining = 10 * 1024 * 1024 + 1;
                while (remaining > 0)
                {
                    var count = Math.Min(block.Length, remaining);
                    stream.Write(block, 0, count);
                    remaining -= count;
                }
            }
        }
        return output.ToArray();
    }

    private sealed class HandlerRuntimeClient(SkillRuntimeHandler handler) : AppRuntime.ISkillOwnerClient
    {
        public SkillCommand? LastCommand { get; private set; }

        public async ValueTask<AppRuntime.SkillProjection> QueryAsync(CancellationToken cancellationToken = default)
            => ToApp(await handler.HandleAsync(new SkillQuery(), cancellationToken));

        public async ValueTask<AppRuntime.SkillProjection> CommandAsync(
            AppRuntime.SkillCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = new SkillCommand(Enum.Parse<SkillCommandKind>(command.Kind.ToString()), command.Value);
            return ToApp(await handler.HandleAsync(LastCommand, cancellationToken));
        }

        public async IAsyncEnumerable<AppRuntime.SkillChanged> SubscribeChangesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static AppRuntime.SkillProjection ToApp(SkillProjection projection)
            => JsonSerializer.Deserialize<AppRuntime.SkillProjection>(JsonSerializer.SerializeToUtf8Bytes(projection))!;
    }

    internal sealed class TestContext(
        string root,
        IPackageFileStore files,
        IPackageKeyValueStore? state = null) : IPackageContext
    {
        public string PackageId => "sunder.package.agent.skills";
        public string Version => "1.0.0";
        public string ContentRootPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage { get; } = new TestStorage(
            root,
            files,
            state ?? new MemoryStateStore());
        public IPackageSettings Settings { get; } = new EmptySettings();
        public IPackageSecrets Secrets { get; } = new EmptySecrets();
        public IPackageLogging Logging => NullPackageLogging.Instance;
    }

    private sealed class TestStorage(
        string root,
        IPackageFileStore files,
        IPackageKeyValueStore state) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = files;
        public IPackageKeyValueStore State { get; } = state;
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestWorkspace(root);
    }

    private sealed class TestWorkspace : IPackageRoleLocalWorkspace
    {
        public TestWorkspace(string root) { WorkspaceRootPath = root; Directory.CreateDirectory(root); }
        public string WorkspaceRootPath { get; }
        public string GetLocalPath(string relativePath) => Path.Combine(WorkspaceRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class MemoryFileStore : IPackageFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private int _writeCount;
        public int? FailAfterWriteCall { get; init; }
        public IReadOnlyCollection<string> Paths => _files.Keys;
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            ValidatePath(relativePath);
            return Task.FromResult(_files.TryGetValue(relativePath, out var value) ? value.ToArray() : null);
        }
        public Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
        {
            ValidatePath(relativePath);
            if (!PackageStorageValidation.IsValidFileLength(contents.Length)) throw new ArgumentException("Invalid test file length.", nameof(contents));
            _files[relativePath] = contents.ToArray();
            if (Interlocked.Increment(ref _writeCount) == FailAfterWriteCall)
            {
                throw new IOException("Injected ambiguous transfer upload failure.");
            }
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            ValidatePath(relativePath);
            _files.Remove(relativePath);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStateStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) { ValidateKey(key); return Task.FromResult(_values.GetValueOrDefault(key)); }
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) { ValidateKey(key); ValidateValue(value); _values[key] = value; return Task.CompletedTask; }
        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) { ValidateKey(key); return Task.FromResult(_values.ContainsKey(key)); }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) { ValidateKey(key); _values.Remove(key); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            ValidatePrefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
    }

    private sealed class EmptySettings : IPackageSettings
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptySecrets : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) { ValidateKey(key); return Task.FromResult<string?>(null); }
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) { ValidateKey(key); ValidateValue(value); return Task.CompletedTask; }
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) { ValidateKey(key); return Task.CompletedTask; }
    }

    private sealed class UnusedGitHubClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static void ValidateKey(string? key)
    {
        if (!PackageStorageValidation.IsValidKey(key)) throw new ArgumentException("Invalid test storage key.", nameof(key));
    }

    private static void ValidatePrefix(string? prefix)
    {
        if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix)) throw new ArgumentException("Invalid test storage prefix.", nameof(prefix));
    }

    private static void ValidateValue(string? value)
    {
        if (!PackageStorageValidation.IsValidValue(value)) throw new ArgumentException("Invalid test storage value.", nameof(value));
    }

    private static void ValidatePath(string? path)
    {
        if (!PackageStorageValidation.IsValidRelativePath(path)) throw new ArgumentException("Invalid test storage path.", nameof(path));
    }
}

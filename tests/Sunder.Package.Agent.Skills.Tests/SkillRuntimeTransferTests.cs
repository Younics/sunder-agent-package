using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Runtime;
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
            Assert.Single(store.ListSkills());
            Assert.Equal("runtime-readable content", await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(skill), "resource.txt")));
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

    private sealed class HandlerRuntimeClient(SkillRuntimeHandler handler) : IPackageRuntimeClient
    {
        public bool IsAvailable => true;
        public SkillCommand? LastCommand { get; private set; }

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(PackageRuntimeOperation<TRequest, TResponse> operation, TRequest request, CancellationToken cancellationToken = default)
            where TRequest : class where TResponse : class
        {
            if (request is SkillCommand command)
            {
                LastCommand = command;
                return (TResponse)(object)await handler.HandleAsync(command, cancellationToken);
            }
            if (request is SkillQuery query)
            {
                return (TResponse)(object)await handler.HandleAsync(query, cancellationToken);
            }
            throw new InvalidOperationException();
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(PackageRuntimeStream<TRequest, TEvent> stream, TRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class where TEvent : class
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestContext(string root, IPackageFileStore files) : IPackageContext
    {
        public string PackageId => "sunder.package.agent.skills";
        public string Version => "1.0.0";
        public string ContentRootPath => AppContext.BaseDirectory;
        public IPackageStorageContext Storage { get; } = new TestStorage(root, files);
        public IPackageSettings Settings { get; } = new EmptySettings();
        public IPackageSecrets Secrets { get; } = new EmptySecrets();
        public IPackageLogging Logging => NullPackageLogging.Instance;
    }

    private sealed class TestStorage(string root, IPackageFileStore files) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = files;
        public IPackageKeyValueStore State { get; } = new MemoryStateStore();
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
        public IReadOnlyCollection<string> Paths => _files.Keys;
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(relativePath, out var value) ? value.ToArray() : null);
        public Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default) { _files[relativePath] = contents.ToArray(); return Task.CompletedTask; }
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default) { _files.Remove(relativePath); return Task.CompletedTask; }
    }

    private sealed class MemoryStateStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) { _values.Remove(key); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
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
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnusedGitHubClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

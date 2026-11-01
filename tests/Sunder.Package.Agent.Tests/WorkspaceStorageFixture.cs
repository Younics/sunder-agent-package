using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tests;

internal static class WorkspaceStorageFixture
{
    internal sealed class TestScope : IDisposable
    {
        private readonly TemporaryDirectory _directory;

        private TestScope(TemporaryDirectory directory)
        {
            _directory = directory;
            RootPath = directory.Path;
            Context = new TestPackageContext(RootPath);
        }

        public string RootPath { get; }

        public TestPackageContext Context { get; }

        public static TestScope Create()
            => new(TemporaryDirectory.Create("sunder-execution-tests"));

        public void Dispose() => _directory.Dispose();
    }

    internal sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => "test";

        public string Version { get; } = "1.0.0";

        public string ContentRootPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestSettings();

        public IPackageSecrets Secrets { get; } = new TestSecrets();


        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    internal sealed class TestStorageContext(string rootPath) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new TestPackageFileStore(Path.Combine(rootPath, "files"));

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new TestPackageRoleLocalWorkspace(rootPath);
    }

    internal sealed class TestPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    internal sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public void Seed(string key, string value)
        {
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
    }

    internal sealed class TestSettings : IPackageSettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal)
        {
            ["shell.timeoutSeconds.default"] = "30",
        };

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public void Seed(string key, string value) => _values[key] = value;
    }

    internal sealed class TestSecrets : InMemoryPackageSecrets;
}

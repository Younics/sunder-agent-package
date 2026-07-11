using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestPackageScope : IDisposable
{
    private RegressionTestPackageScope(string rootPath)
    {
        RootPath = rootPath;
        Context = new RegressionTestPackageContext(rootPath);
    }

    public string RootPath { get; }

    public IPackageContext Context { get; }

    public static RegressionTestPackageScope Create()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-regression-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return new RegressionTestPackageScope(rootPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Cleanup should not hide assertion failures.
        }
    }
}

internal sealed class RegressionTestExtensionCatalog : IPackageExtensionCatalog
{
    private readonly Dictionary<string, List<object>> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public void AddExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract extension)
    {
        if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
        {
            entries = [];
            _extensions[extensionPoint.Id] = entries;
        }

        entries.Add(extension!);
    }

    public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
        => !_extensions.TryGetValue(extensionPoint.Id, out var entries)
            ? []
            : entries.Cast<TContract>().ToArray();
}

internal sealed class RegressionTestPackageContext(string rootPath) : IPackageContext
{
    public string PackageId => "test.package.agent";

    public Version Version { get; } = new(1, 0, 0);

    public string InstallPath => AppContext.BaseDirectory;

    public IPackageStorageContext Storage { get; } = new RegressionTestStorageContext(rootPath);

    public IPackageConfiguration Configuration { get; } = new RegressionTestConfiguration();

    public IPackageSecrets Secrets { get; } = new RegressionTestSecrets();

    public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
        Sunder.Sdk.Logging.NullPackageLogging.Instance;
}

internal sealed class RegressionTestStorageContext : IPackageStorageContext
{
    public RegressionTestStorageContext(string rootPath)
    {
        DataRootPath = Path.Combine(rootPath, "data");
        CacheRootPath = Path.Combine(rootPath, "cache");
        LogsRootPath = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(DataRootPath);
        Directory.CreateDirectory(CacheRootPath);
        Directory.CreateDirectory(LogsRootPath);
        Files = new RegressionTestFileStore(Path.Combine(rootPath, "files"));
    }

    public string DataRootPath { get; }

    public string CacheRootPath { get; }

    public string LogsRootPath { get; }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new RegressionTestKeyValueStore();
}

internal sealed class RegressionTestFileStore(string rootPath) : IPackageFileStore
{
    public string RootPath { get; } = rootPath;

    public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);
}

internal sealed class RegressionTestKeyValueStore : IPackageKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string key) => _values.GetValueOrDefault(key);

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(GetValue(key));

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.ContainsKey(key));

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray());
}

internal sealed class RegressionTestConfiguration : IPackageConfiguration
{
    public string? GetValue(string key) => null;
}

internal sealed class RegressionTestSecrets : IPackageSecrets
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? GetSecret(string key) => _values.GetValueOrDefault(key);

    public void SetSecret(string key, string value) => _values[key] = value;

    public void DeleteSecret(string key) => _values.Remove(key);
}

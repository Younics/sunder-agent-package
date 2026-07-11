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

    public string Version { get; } = "1.0.0";

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
        Directory.CreateDirectory(rootPath);
        Files = new RegressionTestFileStore(Path.Combine(rootPath, "files"));
        LocalWorkspace = new TestPackageWorkspaceLease(rootPath);
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new RegressionTestKeyValueStore();

    public IPackageLocalWorkspaceLease LocalWorkspace { get; }
}

internal sealed class RegressionTestFileStore(string rootPath) : IPackageFileStore
{
    private readonly string _rootPath = rootPath;

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_rootPath, relativePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(_rootPath, relativePath));
        return Task.CompletedTask;
    }
}

internal sealed class RegressionTestKeyValueStore : IPackageKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

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
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

internal sealed class RegressionTestSecrets : IPackageSecrets
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
}

using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.TestSupport;

public sealed class ProviderTestPackageContext : IPackageContext
{
    public ProviderTestPackageContext(
        string packageId,
        IReadOnlyDictionary<string, string>? configurationValues = null,
        IReadOnlyDictionary<string, string>? secretValues = null,
        IPackageKeyValueStore? state = null,
        IPackageSecrets? secrets = null)
    {
        PackageId = packageId;
        Storage = new ProviderTestStorageContext(configurationValues, state);
        Configuration = new ProviderTestConfiguration(Storage.State);
        Secrets = secrets ?? new ProviderTestSecrets(secretValues);
    }

    public string PackageId { get; }

    public string Version { get; } = "1.0.0";

    public string InstallPath { get; } = AppContext.BaseDirectory;

    public ProviderTestStorageContext Storage { get; }

    IPackageStorageContext IPackageContext.Storage => Storage;

    public IPackageConfiguration Configuration { get; }

    public IPackageSecrets Secrets { get; }

    public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
}

public sealed class ProviderTestStorageContext : IPackageStorageContext
{
    public ProviderTestStorageContext(
        IReadOnlyDictionary<string, string>? values,
        IPackageKeyValueStore? state = null)
    {
        State = state ?? new ProviderTestKeyValueStore(values);
    }

    public IPackageFileStore Files => throw new NotSupportedException();

    public IPackageKeyValueStore State { get; }

    IPackageKeyValueStore IPackageStorageContext.State => State;

    public IPackageLocalWorkspaceLease LocalWorkspace { get; } = new ProviderTestWorkspace();
}

public sealed class ProviderTestKeyValueStore : IPackageKeyValueStore
{
    private readonly Dictionary<string, string> _values;

    public ProviderTestKeyValueStore(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.ContainsKey(key));

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.Remove(key));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
}

public sealed class ProviderTestConfiguration(IPackageKeyValueStore state) : IPackageConfiguration
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => state.GetValueAsync(key, cancellationToken);
}

public sealed class ProviderTestSecrets : IPackageSecrets
{
    private readonly Dictionary<string, string> _values;

    public ProviderTestSecrets(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class ProviderTestWorkspace : IPackageLocalWorkspaceLease
{
    public string WorkspaceRootPath => AppContext.BaseDirectory;
    public string GetLocalPath(string relativePath) => Path.GetFullPath(Path.Combine(WorkspaceRootPath, relativePath));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

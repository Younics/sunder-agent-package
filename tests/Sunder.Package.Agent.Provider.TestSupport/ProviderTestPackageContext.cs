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

    public Version Version { get; } = new(1, 0, 0);

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

    public string DataRootPath { get; } = AppContext.BaseDirectory;

    public string CacheRootPath { get; } = AppContext.BaseDirectory;

    public string LogsRootPath { get; } = AppContext.BaseDirectory;

    public IPackageFileStore Files => throw new NotSupportedException();

    public IPackageKeyValueStore State { get; }

    IPackageKeyValueStore IPackageStorageContext.State => State;
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

    public string? GetValue(string key) => _values.GetValueOrDefault(key);

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(GetValue(key));

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
    public string? GetValue(string key) => state.GetValue(key);
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

    public string? GetSecret(string key) => _values.GetValueOrDefault(key);

    public void SetSecret(string key, string value) => _values[key] = value;

    public void DeleteSecret(string key) => _values.Remove(key);
}

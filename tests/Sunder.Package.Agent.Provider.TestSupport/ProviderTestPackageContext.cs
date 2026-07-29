using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Provider.TestSupport;

public sealed class ProviderTestPackageContext : IPackageContext
{
    public ProviderTestPackageContext(
        string packageId,
        IReadOnlyDictionary<string, string>? configurationValues = null,
        IReadOnlyDictionary<string, string>? secretValues = null,
        IPackageKeyValueStore? state = null,
        IPackageSecrets? secrets = null,
        IPackageKeyValueStore? settings = null,
        IPackageCallbackClient? callbacks = null)
    {
        PackageId = packageId;
        Storage = new ProviderTestStorageContext(state);
        Settings = new ProviderTestSettings(settings ?? new ProviderTestKeyValueStore(configurationValues));
        Secrets = secrets ?? new ProviderTestSecrets(secretValues);
        Callbacks = callbacks ?? NullPackageCallbackClient.Instance;
    }

    public string PackageId { get; }

    public string Version { get; } = "1.0.0";

    public string ContentRootPath { get; } = AppContext.BaseDirectory;

    public ProviderTestStorageContext Storage { get; }

    IPackageStorageContext IPackageContext.Storage => Storage;

    public IPackageSettings Settings { get; }

    public IPackageSecrets Secrets { get; }

    public IPackageCallbackClient Callbacks { get; }


    public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
}

public sealed class ProviderTestStorageContext : IPackageStorageContext
{
    public ProviderTestStorageContext(
        IPackageKeyValueStore? state = null)
    {
        State = state ?? new ProviderTestKeyValueStore();
    }

    public IPackageFileStore Files => throw new NotSupportedException();

    public IPackageKeyValueStore State { get; }

    IPackageKeyValueStore IPackageStorageContext.State => State;

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new ProviderTestWorkspace();
}

public sealed class ProviderTestKeyValueStore : IPackageKeyValueStore
{
    private readonly Dictionary<string, string> _values;

    public ProviderTestKeyValueStore(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
        ValidateValues(_values);
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ValidateValue(value);
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        return Task.FromResult(_values.ContainsKey(key));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        return Task.FromResult(_values.Remove(key));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException("Invalid test package storage prefix.", nameof(prefix));
        }
        return Task.FromResult<IReadOnlyList<string>>(
            _values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static void ValidateValues(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            ValidateKey(pair.Key);
            ValidateValue(pair.Value);
        }
    }

    private static void ValidateKey(string? key)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException("Invalid test package storage key.", nameof(key));
        }
    }

    private static void ValidateValue(string? value)
    {
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException("Invalid test package storage value.", nameof(value));
        }
    }
}

public sealed class ProviderTestSettings(IPackageKeyValueStore settings) : IPackageSettings
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => settings.GetValueAsync(key, cancellationToken);

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => settings.GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        => settings.SetValueAsync(key, value, cancellationToken);

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        => settings.DeleteValueAsync(key, cancellationToken);
}

public sealed class ProviderTestSecrets : IPackageSecrets
{
    private readonly Dictionary<string, string> _values;

    public ProviderTestSecrets(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var pair in _values)
        {
            ValidateKey(pair.Key);
            ValidateValue(pair.Value);
        }
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ValidateValue(value);
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        _values.Remove(key);
        return Task.CompletedTask;
    }

    private static void ValidateKey(string? key)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException("Invalid test package secret key.", nameof(key));
        }
    }

    private static void ValidateValue(string? value)
    {
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException("Invalid test package secret value.", nameof(value));
        }
    }
}

internal sealed class ProviderTestWorkspace : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath => AppContext.BaseDirectory;
    public string GetLocalPath(string relativePath) => Path.GetFullPath(Path.Combine(WorkspaceRootPath, relativePath));
}

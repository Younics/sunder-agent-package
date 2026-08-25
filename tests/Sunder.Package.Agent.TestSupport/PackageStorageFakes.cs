using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.TestSupport;

public class TestPackageRoleLocalWorkspace(string rootPath) : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath { get; } = System.IO.Path.GetFullPath(rootPath);

    public string GetLocalPath(string relativePath)
    {
        TestPackageStorageValidation.RelativePath(relativePath);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(WorkspaceRootPath, relativePath));
        var prefix = WorkspaceRootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                     + System.IO.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path escapes the test workspace.", nameof(relativePath));
        }
        return path;
    }
}

public class DirectoryPackageFileStore(string rootPath) : IPackageFileStore
{
    protected string RootPath { get; } = System.IO.Path.GetFullPath(rootPath);

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        TestPackageStorageValidation.FileLength(contents.Length);
        var path = Resolve(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Resolve(relativePath));
        return Task.CompletedTask;
    }

    private string Resolve(string relativePath)
    {
        TestPackageStorageValidation.RelativePath(relativePath);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootPath, relativePath));
        var prefix = RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                     + System.IO.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path escapes the test file store.", nameof(relativePath));
        }
        return path;
    }
}

public sealed class UnsupportedPackageFileStore : IPackageFileStore
{
    public static UnsupportedPackageFileStore Instance { get; } = new();

    private UnsupportedPackageFileStore()
    {
    }

    public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
        => Unsupported<byte[]>(relativePath, cancellationToken);

    public Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
        => Unsupported<object>(relativePath, cancellationToken);

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        => Unsupported<object>(relativePath, cancellationToken);

    private static Task<T?> Unsupported<T>(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.RelativePath(relativePath);
        return Task.FromException<T?>(new NotSupportedException("This test context did not compose a package file store."));
    }
}

public class InMemoryPackageKeyValueStore : IPackageKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values;
    private AsyncGate? _nextWrite;

    public InMemoryPackageKeyValueStore(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
            : new ConcurrentDictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var pair in _values)
        {
            TestPackageStorageValidation.Key(pair.Key);
            TestPackageStorageValidation.Value(pair.Value);
        }
    }

    public AsyncGate BlockNextWrite()
    {
        var gate = new AsyncGate();
        if (Interlocked.CompareExchange(ref _nextWrite, gate, null) is not null)
        {
            throw new InvalidOperationException("A write is already blocked.");
        }
        return gate;
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        TestPackageStorageValidation.Value(value);
        var gate = Interlocked.Exchange(ref _nextWrite, null);
        if (gate is not null)
        {
            await gate.EnterAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        return Task.FromResult(_values.ContainsKey(key));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Prefix(prefix);
        return Task.FromResult<IReadOnlyList<string>>(_values.Keys
            .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray());
    }
}

public class InMemoryPackageSettings : IPackageSettings
{
    private readonly IReadOnlyDictionary<string, PackageSettingsField>? _fields;
    private readonly IPackageKeyValueStore _values;

    public InMemoryPackageSettings(
        IReadOnlyDictionary<string, string>? values = null,
        PackageSettingsSchema? schema = null)
        : this(new InMemoryPackageKeyValueStore(values), schema)
    {
    }

    public InMemoryPackageSettings(
        IPackageKeyValueStore values,
        PackageSettingsSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
        _fields = schema?.Sections
            .SelectMany(static section => section.Fields)
            .ToDictionary(static field => field.Key, StringComparer.Ordinal);
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var field = ValidateKey(key);
        return await _values.GetValueAsync(key, cancellationToken).ConfigureAwait(false)
               ?? field?.DefaultValue;
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = ValidateKey(key);
        return _values.GetValueAsync(key, cancellationToken);
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var field = ValidateKey(key);
        TestPackageStorageValidation.Value(value);
        if (field is not null)
        {
            ValidateValue(field, value);
        }
        return _values.SetValueAsync(key, value, cancellationToken);
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var field = ValidateKey(key);
        if (field is { IsRequired: true, DefaultValue: null })
        {
            throw new ArgumentException(
                $"Required setting '{key}' cannot be deleted because it has no default value.",
                nameof(key));
        }
        return _values.DeleteValueAsync(key, cancellationToken);
    }

    private PackageSettingsField? ValidateKey(string key)
    {
        TestPackageStorageValidation.Key(key);
        if (_fields is null)
        {
            return null;
        }
        if (!_fields.TryGetValue(key, out var field) || field.Kind == PackageSettingsFieldKind.Secret)
        {
            throw new ArgumentException($"Setting '{key}' is not a declared non-secret field.", nameof(key));
        }
        return field;
    }

    private static void ValidateValue(PackageSettingsField field, string value)
    {
        if (field.IsRequired && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Required setting '{field.Key}' cannot be empty.", nameof(value));
        }
        if (field.Kind == PackageSettingsFieldKind.Boolean && !bool.TryParse(value, out _))
        {
            throw new ArgumentException($"Boolean setting '{field.Key}' has invalid value '{value}'.", nameof(value));
        }
        if (field.Kind == PackageSettingsFieldKind.Select
            && !field.Options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Select setting '{field.Key}' has undeclared value '{value}'.", nameof(value));
        }
    }
}

public class NoOpPackageSettings : IPackageSettings
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        TestPackageStorageValidation.Value(value);
        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        return Task.CompletedTask;
    }
}

public class InMemoryPackageSecrets : IPackageSecrets
{
    private readonly ConcurrentDictionary<string, string> _values;

    public InMemoryPackageSecrets(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
            : new ConcurrentDictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var pair in _values)
        {
            TestPackageStorageValidation.Key(pair.Key);
            TestPackageStorageValidation.Value(pair.Value);
        }
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        TestPackageStorageValidation.Value(value);
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageValidation.Key(key);
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public static class TestPackageStorageValidation
{
    public static void Key(string? key)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException("Test package storage key violates PackageStorageValidation.", nameof(key));
        }
    }

    public static void Prefix(string? prefix)
    {
        if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException("Test package storage prefix violates PackageStorageValidation.", nameof(prefix));
        }
    }

    public static void Value(string? value)
    {
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException("Test package storage value violates PackageStorageValidation.", nameof(value));
        }
    }

    public static void RelativePath(string? relativePath)
    {
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException("Test package storage path violates PackageStorageValidation.", nameof(relativePath));
        }
    }

    public static void FileLength(long byteCount)
    {
        if (!PackageStorageValidation.IsValidFileLength(byteCount))
        {
            throw new ArgumentException("Test package file length violates PackageStorageValidation.", nameof(byteCount));
        }
    }
}

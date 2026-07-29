using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Tests;

internal class TestPackageRoleLocalWorkspace(string rootPath) : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath { get; } = Path.GetFullPath(rootPath);

    public string GetLocalPath(string relativePath)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        var path = Path.GetFullPath(Path.Combine(WorkspaceRootPath, relativePath));
        var prefix = WorkspaceRootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path escapes the test workspace.", nameof(relativePath));
        }

        return path;
    }
}

internal class TestPackageFileStoreBase(string rootPath) : IPackageFileStore
{
    protected string RootPath { get; } = rootPath;

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        var path = Path.Combine(RootPath, relativePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        TestPackageStorageGuards.FileLength(contents.Length);
        var path = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.RelativePath(relativePath);
        File.Delete(Path.Combine(RootPath, relativePath));
        return Task.CompletedTask;
    }
}

internal class EmptyPackageSettings : IPackageSettings
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        TestPackageStorageGuards.Value(value);
        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.CompletedTask;
    }
}

internal class InMemoryPackageSecrets : IPackageSecrets
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        TestPackageStorageGuards.Value(value);
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal static class TestPackageStorageGuards
{
    internal static void Key(string? key)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException("Test package storage key violates PackageStorageValidation.", nameof(key));
        }
    }

    internal static void Prefix(string? prefix)
    {
        if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException("Test package storage prefix violates PackageStorageValidation.", nameof(prefix));
        }
    }

    internal static void Value(string? value)
    {
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException("Test package storage value violates PackageStorageValidation.", nameof(value));
        }
    }

    internal static void RelativePath(string? relativePath)
    {
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException("Test package storage path violates PackageStorageValidation.", nameof(relativePath));
        }
    }

    internal static void FileLength(long byteCount)
    {
        if (!PackageStorageValidation.IsValidFileLength(byteCount))
        {
            throw new ArgumentException("Test package file length violates PackageStorageValidation.", nameof(byteCount));
        }
    }
}

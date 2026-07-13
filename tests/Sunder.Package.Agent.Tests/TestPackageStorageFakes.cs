using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tests;

internal class TestPackageRoleLocalWorkspace(string rootPath) : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath { get; } = Path.GetFullPath(rootPath);

    public string GetLocalPath(string relativePath)
    {
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
        var path = Path.Combine(RootPath, relativePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(RootPath, relativePath));
        return Task.CompletedTask;
    }
}

internal class EmptyPackageSettings : IPackageSettings
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => GetValueAsync(key, cancellationToken);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal class InMemoryPackageSecrets : IPackageSecrets
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

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
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

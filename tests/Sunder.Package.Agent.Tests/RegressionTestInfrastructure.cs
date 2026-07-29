using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Sunder.Sdk.Abstractions;
using Xunit;

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
        var temporaryRoot = OperatingSystem.IsMacOS()
            ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            : Path.GetTempPath();
        var rootPath = Path.Combine(
            temporaryRoot,
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

internal sealed class RegressionTestExtensionCatalog :
    IPackageExtensionCatalog,
    IPackageExtensionInvocationCatalog
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, List<OwnedExtension>> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public List<(string PackageId, Exception Exception)> FaultReports { get; } = [];

    public void AddExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract extension)
        => AddExtension(extensionPoint, extension, "test.package");

    public void AddExtension<TContract>(
        PackageExtensionPoint<TContract> extensionPoint,
        TContract extension,
        string packageId)
    {
        lock (_syncRoot)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
            {
                entries = [];
                _extensions[extensionPoint.Id] = entries;
            }

            entries.Add(new OwnedExtension(packageId, extension!));
        }
    }

    public void RemoveExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract extension)
        => _ = RetireExtensionAsync(extensionPoint, extension);

    public async Task RetireExtensionAsync<TContract>(
        PackageExtensionPoint<TContract> extensionPoint,
        TContract extension)
    {
        OwnedExtension[] removed;
        lock (_syncRoot)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
            {
                return;
            }

            removed = entries.Where(entry => ReferenceEquals(entry.Extension, extension)).ToArray();
            foreach (var entry in removed)
            {
                entry.Active = false;
            }
            entries.RemoveAll(entry => !entry.Active);
        }

        var cancellations = new Task[removed.Length];
        for (var index = 0; index < removed.Length; index++)
        {
            var entry = removed[index];
            cancellations[index] = entry.Retirement.CancelAsync();
            lock (_syncRoot)
            {
                CompleteRetirementIfDrained(entry);
            }
        }

        try
        {
            await Task.WhenAll(cancellations).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(removed.Select(static entry => entry.RetirementCompleted.Task)).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        lock (_syncRoot)
        {
            return !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries.Select(static entry => entry.Extension).Cast<TContract>().ToArray();
        }
    }

    public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        lock (_syncRoot)
        {
            return !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries.Select(entry => new PackageExtensionContribution<TContract>(
                    entry.PackageId,
                    (TContract)entry.Extension))
                .ToArray();
        }
    }

    public IReadOnlyList<IPackageExtensionReference<TContract>> GetExtensionReferences<TContract>(
        PackageExtensionPoint<TContract> extensionPoint)
    {
        lock (_syncRoot)
        {
            return !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries
                    .Select(entry => (IPackageExtensionReference<TContract>)new ExtensionReference<TContract>(this, entry))
                    .ToArray();
        }
    }

    public bool TryReportInvariantViolation<TContract>(
        IPackageExtensionReference<TContract> reference,
        Exception exception)
    {
        lock (_syncRoot)
        {
            if (reference is not ExtensionReference<TContract> ownedReference
                || !ReferenceEquals(ownedReference.Catalog, this)
                || !ownedReference.Entry.Active)
            {
                return false;
            }

            FaultReports.Add((ownedReference.Entry.PackageId, exception));
            return true;
        }
    }

    private bool TryAcquire<TContract>(OwnedExtension entry, out IPackageExtensionLease<TContract>? lease)
    {
        lock (_syncRoot)
        {
            if (!entry.Active || entry.Extension is not TContract contribution)
            {
                lease = null;
                return false;
            }

            entry.LeaseCount++;
            lease = new ExtensionLease<TContract>(this, entry, contribution);
            return true;
        }
    }

    private void Release(OwnedExtension entry)
    {
        lock (_syncRoot)
        {
            entry.LeaseCount--;
            CompleteRetirementIfDrained(entry);
        }
    }

    private static void CompleteRetirementIfDrained(OwnedExtension entry)
    {
        if (!entry.Active && entry.LeaseCount == 0)
        {
            entry.RetirementCompleted.TrySetResult();
        }
    }

    private sealed class ExtensionReference<TContract>(
        RegressionTestExtensionCatalog catalog,
        OwnedExtension entry) : IPackageExtensionReference<TContract>
    {
        internal RegressionTestExtensionCatalog Catalog { get; } = catalog;

        internal OwnedExtension Entry { get; } = entry;

        public bool TryAcquire([NotNullWhen(true)] out IPackageExtensionLease<TContract>? lease)
            => Catalog.TryAcquire(Entry, out lease);
    }

    private sealed class ExtensionLease<TContract>(
        RegressionTestExtensionCatalog catalog,
        OwnedExtension entry,
        TContract contribution)
        : IPackageExtensionLease<TContract>
    {
        private object? _contribution = contribution;

        public string PackageId
        {
            get
            {
                ThrowIfDisposed();
                return entry.PackageId;
            }
        }

        public TContract Contribution
            => (TContract)(Volatile.Read(ref _contribution)
                ?? throw new ObjectDisposedException(nameof(IPackageExtensionLease<TContract>)));

        public CancellationToken RetirementToken
        {
            get
            {
                ThrowIfDisposed();
                return entry.Retirement.Token;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _contribution, null) is not null)
            {
                catalog.Release(entry);
            }
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _contribution) is null, this);
    }

    private sealed class OwnedExtension(string packageId, object extension)
    {
        public string PackageId { get; } = packageId;
        public object Extension { get; } = extension;
        public CancellationTokenSource Retirement { get; } = new();
        public TaskCompletionSource RetirementCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LeaseCount { get; set; }
        public bool Active { get; set; } = true;
    }
}

internal sealed class RegressionTestPackageContext(string rootPath) : IPackageContext
{
    public string PackageId => "test.package.agent";

    public string Version { get; } = "1.0.0";

    public string ContentRootPath => AppContext.BaseDirectory;

    public IPackageStorageContext Storage { get; } = new RegressionTestStorageContext(rootPath);

    public IPackageSettings Settings { get; } = new RegressionTestSettings();

    public IPackageSecrets Secrets { get; } = new RegressionTestSecrets();


    public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
        Sunder.Sdk.Logging.NullPackageLogging.Instance;
}

internal sealed class RegressionTestStorageContext : IPackageStorageContext
{
    public RegressionTestStorageContext(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        Files = new RegressionTestFileStore(Path.Combine(rootPath, "files"));
        RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new RegressionTestKeyValueStore();

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
}

internal sealed class RegressionTestFileStore(string rootPath) : IPackageFileStore
{
    private readonly string _rootPath = rootPath;

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        var path = Path.Combine(_rootPath, relativePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        TestPackageStorageGuards.FileLength(contents.Length);
        var path = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.RelativePath(relativePath);
        File.Delete(Path.Combine(_rootPath, relativePath));
        return Task.CompletedTask;
    }
}

internal sealed class RegressionTestKeyValueStore : IPackageKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private WriteBlock? _nextWrite;

    public WriteBlock BlockNextWrite()
    {
        var block = new WriteBlock();
        Assert.Null(Interlocked.CompareExchange(ref _nextWrite, block, null));
        return block;
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public async Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        TestPackageStorageGuards.Value(value);
        var block = Interlocked.Exchange(ref _nextWrite, null);
        if (block is not null)
        {
            block.Started.TrySetResult();
            await block.Release.Task.WaitAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
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
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Prefix(prefix);
        return Task.FromResult<IReadOnlyList<string>>(
            _values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    internal sealed class WriteBlock
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class RegressionTestSettings : EmptyPackageSettings
{
}

internal sealed class RegressionTestSecrets : IPackageSecrets
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

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
        _values.Remove(key);
        return Task.CompletedTask;
    }
}

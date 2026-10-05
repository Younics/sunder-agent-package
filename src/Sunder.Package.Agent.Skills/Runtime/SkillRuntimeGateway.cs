using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Shared.Importing;
#if SUNDER_SKILLS_APP
using Sunder.Package.Agent.Shared.Presentation;
#endif
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Skills.Runtime;

#if SUNDER_SKILLS_WORKER
internal sealed class SkillLocalManagementGateway(SkillStore store, SkillImportService importer)
    : ISkillManagementGateway
{
    public event Action? SkillsChanged
    {
        add => store.SkillsChanged += value;
        remove => store.SkillsChanged -= value;
    }

    public Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(store.ListSkills());
    }

    public Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(string url, CancellationToken cancellationToken = default)
        => importer.ImportGitHubAsync(url, cancellationToken);

    public Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(CancellationToken cancellationToken = default)
        => importer.ImportCommonSkillFoldersAsync(cancellationToken);

    public Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(string folderPath, CancellationToken cancellationToken = default)
        => importer.ImportLocalSkillsAsync(folderPath, cancellationToken);

    public Task DeleteAsync(string skillId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.DeleteSkill(skillId);
        return Task.CompletedTask;
    }
}
#endif

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class SkillRuntimeExceptionPolicy
{
    public static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or System.Runtime.InteropServices.SEHException
           || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsFatal)
           || exception.InnerException is { } inner && IsFatal(inner);
}

#if SUNDER_SKILLS_APP
internal sealed partial class SkillAppRuntimeGateway :
    ISkillManagementGateway,
    IOwnerStreamAvailability,
    IDisposable,
    IAsyncDisposable
{
    private readonly ISkillOwnerClient _client;
    private readonly IPackageContext _context;
    private readonly ILogger<SkillAppRuntimeGateway> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OwnerStreamObserver _observer;
    private readonly Task _observationTask;
    private int _disposed;
    private int _lifetimeDisposed;

    public SkillAppRuntimeGateway(ISunderRpcClient client, IPackageContext context)
        : this(new SkillOwnerClient(client), context)
    {
    }

    internal SkillAppRuntimeGateway(ISkillOwnerClient client, IPackageContext context)
    {
        _client = client;
        _context = context;
        _logger = context.Logging.LoggerFactory.CreateLogger<SkillAppRuntimeGateway>();
        _pendingTransferCleanupTask = CleanupPendingTransfersAsync();
        _observer = new OwnerStreamObserver(LogObservationRetry);
        _observationTask = _observer.RunAsync(ObserveChangesOnceAsync, _lifetime.Token);
    }

    public event Action? SkillsChanged;
    public bool IsOwnerStreamAvailable => _observer.IsAvailable;
    public event Action<bool>? OwnerStreamAvailabilityChanged
    {
        add => _observer.AvailabilityChanged += value;
        remove => _observer.AvailabilityChanged -= value;
    }

    internal Task ObservationTask => _observationTask;

    public async Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(CancellationToken cancellationToken = default)
        => (await _client.QueryAsync(cancellationToken).ConfigureAwait(false)).Skills;

    public async Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(string url, CancellationToken cancellationToken = default)
        => await InvokeImportedAsync(new SkillCommand(SkillCommandKind.ImportGitHub, url), cancellationToken);

    public async Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(CancellationToken cancellationToken = default)
        => await InvokeImportedAsync(new SkillCommand(SkillCommandKind.ImportCommon), cancellationToken);

    public async Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var transferId = Guid.NewGuid().ToString("N");
        var transferRoot = $"skill-import-transfers/{transferId}";
        var temporaryArchive = _context.Storage.RoleLocalWorkspace.GetLocalPath($"skill-transfer/{transferId}.zip");
        var uploadedPaths = new List<string>();
        var cleanupRegistered = false;
        try
        {
            await _pendingTransferCleanupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await RegisterTransferCleanupAsync(transferId, cancellationToken).ConfigureAwait(false);
            cleanupRegistered = true;
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryArchive)!);
            await CreateBoundedArchiveAsync(folderPath, temporaryArchive, cancellationToken)
                .ConfigureAwait(false);
            var archiveLength = new FileInfo(temporaryArchive).Length;
            if (archiveLength > SkillTransferLimits.MaxArchiveBytes)
            {
                throw new InvalidOperationException("The compressed skill transfer exceeds the 50 MB limit.");
            }

            var hash = await ComputeHashAsync(temporaryArchive, cancellationToken).ConfigureAwait(false);
            var chunkCount = 0;
            await using (var archive = File.OpenRead(temporaryArchive))
            {
                var buffer = new byte[SkillTransferLimits.ChunkBytes];
                int read;
                while ((read = await archive.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    var path = $"{transferRoot}/chunks/{chunkCount:D4}.bin";
                    uploadedPaths.Add(path);
                    await _context.Storage.Files.WriteAsync(path, buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    chunkCount++;
                }
            }

            var manifestPath = $"{transferRoot}/manifest.json";
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new SkillTransferManifest(
                chunkCount,
                archiveLength,
                hash));
            uploadedPaths.Add(manifestPath);
            await _context.Storage.Files.WriteAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return await InvokeImportedAsync(
                new SkillCommand(SkillCommandKind.ImportTransfer, transferId),
                cancellationToken);
        }
        finally
        {
            var cleanupSucceeded = true;
            foreach (var path in uploadedPaths)
            {
                cleanupSucceeded &= await DeleteUploadedPathAsync(path).ConfigureAwait(false);
            }
            cleanupSucceeded &= await DeleteTemporaryArchiveAsync(temporaryArchive).ConfigureAwait(false);
            if (cleanupRegistered && cleanupSucceeded)
            {
                await DeleteTransferMarkerAsync(transferId).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteAsync(string skillId, CancellationToken cancellationToken = default)
        => _ = await _client.CommandAsync(
            new SkillCommand(SkillCommandKind.Delete, skillId),
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<InstalledSkillRecord>> InvokeImportedAsync(
        SkillCommand command,
        CancellationToken cancellationToken)
    {
        var projection = await _client.CommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
        return projection.Skills
            .Where(skill => projection.ImportedSkillIds.Contains(skill.SkillId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static async Task CreateBoundedArchiveAsync(
        string folderPath,
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new InvalidOperationException("Select an existing skill folder.");
        }

        var files = BoundedImportIO.ValidateTree(
            folderPath,
            new ImportTreeLimits(
                SkillTransferLimits.MaxFiles,
                SkillTransferLimits.MaxFiles,
                SkillTransferLimits.MaxPathDepth,
                SkillTransferLimits.MaxFileBytes,
                SkillTransferLimits.MaxExpandedBytes),
            cancellationToken);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Fastest);
            await using var destination = entry.Open();
            await BoundedImportIO.CopyFileToAsync(file, destination, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    public void Dispose()
    {
        StopObservation();
        DisposeLifetime();
    }

    public async ValueTask DisposeAsync()
    {
        StopObservation();
        try
        {
            await Task.WhenAll(_observationTask, _pendingTransferCleanupTask).ConfigureAwait(false);
        }
        finally
        {
            DisposeLifetime();
        }
    }

    private void StopObservation()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
        {
            _lifetime.Dispose();
        }
    }
}
#endif

#if SUNDER_SKILLS_WORKER
internal sealed partial class SkillRuntimeHandler(
    SkillStore store,
    SkillImportService importer,
    IPackageContext context)
{
    private readonly ILogger<SkillRuntimeHandler> _logger = context.Logging.LoggerFactory
        .CreateLogger<SkillRuntimeHandler>();
    private readonly Lazy<Task> _orphanedTransferCleanup = new(() => CleanupOrphanedTransferStagingAsync(context));

    internal Task OrphanedTransferCleanup => _orphanedTransferCleanup.Value;
    internal Task StartedTransferCleanup => _orphanedTransferCleanup.IsValueCreated ? _orphanedTransferCleanup.Value : Task.CompletedTask;

    public ValueTask<SkillProjection> HandleAsync(SkillQuery request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Project([]));
    }

    public async ValueTask<SkillProjection> HandleAsync(
        SkillCommand request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InstalledSkillRecord> imported = request.Kind switch
        {
            SkillCommandKind.ImportGitHub => await importer.ImportGitHubAsync(Require(request.Value), cancellationToken),
            SkillCommandKind.ImportCommon => await importer.ImportCommonSkillFoldersAsync(cancellationToken),
            SkillCommandKind.ImportTransfer => await ImportTransferAsync(Require(request.Value), cancellationToken),
            SkillCommandKind.Delete => Delete(Require(request.Value), cancellationToken),
            _ => throw new SkillOwnerOperationException("Unknown skill command."),
        };
        return Project(imported.Select(skill => skill.SkillId).ToArray());
    }

    private IReadOnlyList<InstalledSkillRecord> Delete(string skillId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.DeleteSkill(skillId);
        return [];
    }

    private async Task<IReadOnlyList<InstalledSkillRecord>> ImportTransferAsync(
        string transferId,
        CancellationToken cancellationToken)
    {
        if (transferId.Length != 32 || transferId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new SkillOwnerOperationException("The skill transfer id is invalid.");
        }

        await OrphanedTransferCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);

        var root = $"skill-import-transfers/{transferId}";
        try
        {
            var manifestBytes = await context.Storage.Files.ReadAsync($"{root}/manifest.json", cancellationToken)
                .ConfigureAwait(false)
                ?? throw new SkillOwnerOperationException("The skill transfer manifest was not found.");
            var manifest = ParseTransferManifest(manifestBytes);
            var stagingRoot = context.Storage.RoleLocalWorkspace.GetLocalPath($"skill-transfer/{transferId}");
            var archivePath = stagingRoot + ".zip";
            Directory.CreateDirectory(Path.GetDirectoryName(stagingRoot)!);
            try
            {
                await using (var archive = File.Create(archivePath))
                {
                    for (var index = 0; index < manifest.ChunkCount; index++)
                    {
                        var chunk = await context.Storage.Files.ReadAsync(
                            $"{root}/chunks/{index:D4}.bin",
                            cancellationToken).ConfigureAwait(false)
                            ?? throw new SkillOwnerOperationException("A skill transfer chunk is missing.");
                        if (chunk.Length > SkillTransferLimits.ChunkBytes)
                        {
                            throw new SkillOwnerOperationException("A skill transfer chunk exceeds its bounded limit.");
                        }
                        if (archive.Length + chunk.Length > SkillTransferLimits.MaxArchiveBytes)
                        {
                            throw new SkillOwnerOperationException("The skill transfer exceeds the 50 MB limit.");
                        }
                        await archive.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (new FileInfo(archivePath).Length != manifest.TotalBytes
                    || !string.Equals(await ComputeHashAsync(archivePath, cancellationToken), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SkillOwnerOperationException("The skill transfer failed integrity validation.");
                }

                await ExtractBoundedArchiveAsync(archivePath, stagingRoot, cancellationToken)
                    .ConfigureAwait(false);
                return await importer.ImportTransferredSkillsAsync(stagingRoot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await DeleteTransferPathAsync(stagingRoot, isDirectory: true).ConfigureAwait(false);
                await DeleteTransferPathAsync(archivePath, isDirectory: false).ConfigureAwait(false);
            }
        }
        finally
        {
            await DeleteTransferUploadsAsync(root).ConfigureAwait(false);
        }
    }

    private static async Task ExtractBoundedArchiveAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        BoundedImportIO.ValidateDirectoryPath(destination);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > SkillTransferLimits.MaxFiles)
        {
            throw new SkillOwnerOperationException("The skill transfer contains too many files.");
        }

        var planned = new List<(ZipArchiveEntry Entry, string RelativePath, string TargetPath, bool IsDirectory)>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = string.IsNullOrEmpty(entry.Name);
            ValidateArchiveEntryType(entry, isDirectory);
            var archivePathName = entry.FullName.Replace('\\', '/');
            if (isDirectory)
            {
                archivePathName = archivePathName.TrimEnd('/');
                if (archivePathName.Length == 0)
                {
                    continue;
                }
            }
            string relativePath;
            try
            {
                relativePath = BoundedImportIO.NormalizeRelativePath(
                    archivePathName,
                    SkillTransferLimits.MaxPathDepth);
            }
            catch (InvalidDataException exception)
            {
                throw new SkillOwnerOperationException(
                    "The skill transfer contains an unsafe path.",
                    exception);
            }
            if (!paths.TryAdd(relativePath, isDirectory))
            {
                throw new SkillOwnerOperationException(
                    $"The skill transfer contains duplicate or case-colliding path '{relativePath}'.");
            }
            if (!isDirectory
                && (entry.Length > SkillTransferLimits.MaxFileBytes
                    || (totalBytes = checked(totalBytes + entry.Length)) > SkillTransferLimits.MaxExpandedBytes))
            {
                throw new SkillOwnerOperationException("The extracted skill transfer exceeds its bounded limits.");
            }

            var target = Path.GetFullPath(Path.Combine(
                destination,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, PathComparison))
            {
                throw new SkillOwnerOperationException("The skill transfer contains an unsafe path.");
            }
            planned.Add((entry, relativePath, target, isDirectory));
        }

        foreach (var item in planned)
        {
            var parent = Path.GetDirectoryName(item.RelativePath)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(parent))
            {
                if (paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                {
                    throw new SkillOwnerOperationException(
                        $"The skill transfer path '{item.RelativePath}' is nested beneath file '{parent}'.");
                }
                parent = Path.GetDirectoryName(parent)?.Replace('\\', '/');
            }
        }

        var buffer = new byte[64 * 1024];
        long extractedBytes = 0;
        using var writer = BoundedImportIO.OpenTreeWriter(destination);
        foreach (var item in planned.OrderBy(item => item.RelativePath.Count(character => character == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                writer.CreateDirectory(item.RelativePath);
                continue;
            }

            await using var source = item.Entry.Open();
            await using var target = writer.CreateFile(item.RelativePath);
            long fileBytes = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                fileBytes = checked(fileBytes + read);
                extractedBytes = checked(extractedBytes + read);
                if (fileBytes > item.Entry.Length
                    || fileBytes > SkillTransferLimits.MaxFileBytes
                    || extractedBytes > SkillTransferLimits.MaxExpandedBytes)
                {
                    throw new SkillOwnerOperationException(
                        "The extracted skill transfer exceeds its bounded limits.");
                }
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (fileBytes != item.Entry.Length)
            {
                throw new SkillOwnerOperationException(
                    $"The skill transfer entry '{item.RelativePath}' changed size during extraction.");
            }
        }
        writer.Complete();
    }

    private static void ValidateArchiveEntryType(ZipArchiveEntry entry, bool isDirectory)
    {
        if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new SkillOwnerOperationException("The skill transfer contains a reparse point.");
        }
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixType == 0)
        {
            return;
        }
        var expectedType = isDirectory ? 0x4000 : 0x8000;
        if (unixType != expectedType)
        {
            throw new SkillOwnerOperationException(
                "The skill transfer contains a symbolic link, device, or unsupported entry.");
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private SkillProjection Project(IReadOnlyList<string> importedIds)
        => new(store.ListSkills(), importedIds);

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new SkillOwnerOperationException("A skill command value is required.")
            : value;

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

}

internal sealed class SkillRuntimeChangeStream
{
    private readonly SkillStore _store;
    private readonly Action? _subscriptionEstablished;

    public SkillRuntimeChangeStream(SkillStore store, Action? subscriptionEstablished = null)
    {
        _store = store;
        _subscriptionEstablished = subscriptionEstablished;
    }

    public async IAsyncEnumerable<SkillChanged> SubscribeAsync(
        SkillChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SkillChanged>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        void Changed() => channel.Writer.TryWrite(new SkillChanged());
        _store.SkillsChanged += Changed;
        try
        {
            _subscriptionEstablished?.Invoke();
            yield return new SkillChanged();
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            _store.SkillsChanged -= Changed;
            channel.Writer.TryComplete();
        }
    }
}

#endif

internal static class OwnerJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal static bool ShouldMap(Exception exception)
        => exception is SkillOwnerOperationException;

    internal static SunderRpcException Map(SkillOwnerOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The requested Skills operation failed."
            : exception.Message;
        if (message.Length > 2_048)
        {
            message = message[..2_048];
        }
        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "skills.owner.operation-failed",
            message));
    }
}

internal sealed class SkillOwnerOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);

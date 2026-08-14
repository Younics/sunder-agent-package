using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Package.Agent.Skills.Protocol;
using Sunder.Package.Agent.Skills.Protocol.Generated;
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

#if SUNDER_SKILLS_APP
internal sealed class SkillAppRuntimeGateway : ISkillManagementGateway, IDisposable
{
    private const int MaxFileCount = 2_000;
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const long MaxTotalBytes = 50 * 1024 * 1024;
    private const int ChunkBytes = 8 * 1024 * 1024;
    private readonly ISkillOwnerClient _client;
    private readonly IPackageContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public SkillAppRuntimeGateway(ISunderRpcClient client, IPackageContext context)
        : this(new SkillOwnerClient(client), context)
    {
    }

    internal SkillAppRuntimeGateway(ISkillOwnerClient client, IPackageContext context)
    {
        _client = client;
        _context = context;
        _ = ObserveChangesAsync(_lifetime.Token);
    }

    public event Action? SkillsChanged;

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
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryArchive)!);
        var uploadedPaths = new List<string>();
        try
        {
            CreateBoundedArchive(folderPath, temporaryArchive, cancellationToken);
            var archiveLength = new FileInfo(temporaryArchive).Length;
            if (archiveLength > MaxTotalBytes)
            {
                throw new InvalidOperationException("The compressed skill transfer exceeds the 50 MB limit.");
            }

            var hash = await ComputeHashAsync(temporaryArchive, cancellationToken).ConfigureAwait(false);
            var chunkCount = 0;
            await using (var archive = File.OpenRead(temporaryArchive))
            {
                var buffer = new byte[ChunkBytes];
                int read;
                while ((read = await archive.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    var path = $"{transferRoot}/chunks/{chunkCount:D4}.bin";
                    await _context.Storage.Files.WriteAsync(path, buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    uploadedPaths.Add(path);
                    chunkCount++;
                }
            }

            var manifestPath = $"{transferRoot}/manifest.json";
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new SkillTransferManifest(
                chunkCount,
                archiveLength,
                hash));
            await _context.Storage.Files.WriteAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            uploadedPaths.Add(manifestPath);
            return await InvokeImportedAsync(
                new SkillCommand(SkillCommandKind.ImportTransfer, transferId),
                cancellationToken);
        }
        finally
        {
            foreach (var path in uploadedPaths)
            {
                try
                {
                    await _context.Storage.Files.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            try
            {
                File.Delete(temporaryArchive);
            }
            catch
            {
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

    private async Task ObserveChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var _ in _client.SubscribeChangesAsync(cancellationToken))
                {
                    SkillsChanged?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void CreateBoundedArchive(string folderPath, string archivePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new InvalidOperationException("Select an existing skill folder.");
        }

        var root = Path.GetFullPath(folderPath);
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }).ToArray();
        if (files.Length > MaxFileCount)
        {
            throw new InvalidOperationException($"The selected folder contains more than {MaxFileCount} files.");
        }

        long totalBytes = 0;
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                throw new InvalidOperationException($"Skill file is larger than 10 MB: {Path.GetFileName(file)}");
            }

            totalBytes += info.Length;
            if (totalBytes > MaxTotalBytes)
            {
                throw new InvalidOperationException("The selected skill folder exceeds the 50 MB limit.");
            }

            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidOperationException("The selected folder contains an unsafe path.");
            }

            archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Fastest);
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
#endif

#if SUNDER_SKILLS_WORKER
internal sealed class SkillRuntimeHandler(
    SkillStore store,
    SkillImportService importer,
    IPackageContext context)
{
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

        var root = $"skill-import-transfers/{transferId}";
        var manifestBytes = await context.Storage.Files.ReadAsync($"{root}/manifest.json", cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SkillOwnerOperationException("The skill transfer manifest was not found.");
        var manifest = JsonSerializer.Deserialize<SkillTransferManifest>(manifestBytes)
            ?? throw new SkillOwnerOperationException("The skill transfer manifest is invalid.");
        if (manifest.ChunkCount is < 1 or > 8 || manifest.TotalBytes is < 1 or > 50 * 1024 * 1024)
        {
            throw new SkillOwnerOperationException("The skill transfer exceeds its bounded limits.");
        }

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
                    if (archive.Length + chunk.Length > 50 * 1024 * 1024)
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

            ExtractBoundedArchive(archivePath, stagingRoot);
            return await importer.ImportTransferredSkillsAsync(stagingRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(stagingRoot, recursive: true); } catch { }
            try { File.Delete(archivePath); } catch { }
        }
    }

    private static void ExtractBoundedArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 2_000)
        {
            throw new SkillOwnerOperationException("The skill transfer contains too many files.");
        }

        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > 10 * 1024 * 1024 || (totalBytes += entry.Length) > 50 * 1024 * 1024)
            {
                throw new SkillOwnerOperationException("The extracted skill transfer exceeds its bounded limits.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.Ordinal))
            {
                throw new SkillOwnerOperationException("The skill transfer contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
    }

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

internal sealed class SkillRuntimeChangeStream(SkillStore store)
{
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
        store.SkillsChanged += Changed;
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            store.SkillsChanged -= Changed;
            channel.Writer.TryComplete();
        }
    }
}

#endif

#if SUNDER_SKILLS_APP
internal sealed class SkillOwnerClient(ISunderRpcClient rpc) : ISkillOwnerClient
{
    public async ValueTask<SkillProjection> QueryAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var client = new SkillsManagementClient(scope, endpoint);
        var response = await client.QueryAsync(new OwnerEmpty(), cancellationToken).ConfigureAwait(false);
        return await ReadContentAsync<SkillProjection>(scope, response, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SkillProjection> CommandAsync(
        SkillCommand command,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var client = new SkillsManagementClient(scope, endpoint);
        var request = await RegisterContentAsync(
            scope,
            endpoint,
            command,
            "skills-command.json",
            cancellationToken).ConfigureAwait(false);
        var response = await client.CommandAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadContentAsync<SkillProjection>(scope, response, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SkillChanged> SubscribeChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var client = new SkillsManagementClient(scope, endpoint);
        await foreach (var _ in client.ChangesAsync(new OwnerEmpty(), cancellationToken).ConfigureAwait(false))
        {
            yield return new SkillChanged();
        }
    }

    private async ValueTask<SunderRpcEndpointReference> ResolveEndpointAsync(
        CancellationToken cancellationToken)
    {
        var descriptor = SkillsOwnerProtocol.Descriptor;
        var catalog = await rpc.DiscoverAsync(descriptor.ContractId, cancellationToken).ConfigureAwait(false);
        return catalog.Providers.SingleOrDefault(provider =>
                   string.Equals(provider.ProviderId, SkillsOwnerProtocol.ProviderId, StringComparison.Ordinal)
                   && provider.State == SunderRpcProviderState.Active
                   && string.Equals(provider.AbiSha256, descriptor.AbiSha256, StringComparison.Ordinal))
               ?.Endpoint
               ?? throw new InvalidOperationException("The Skills runtime is unavailable.");
    }

    private static async ValueTask<OwnerContent> RegisterContentAsync<T>(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, OwnerJson.Options);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await scope.RegisterContentAsync(
            endpoint,
            stream,
            new SunderRpcContentRegistrationOptions(
                "application/json",
                fileName,
                bytes.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        return new OwnerContent { Content = content };
    }

    private static async ValueTask<T> ReadContentAsync<T>(
        ISunderRpcCallScope scope,
        OwnerContent response,
        CancellationToken cancellationToken)
        where T : notnull
    {
        await using var stream = await scope.OpenContentAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, OwnerJson.Options, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("The Skills runtime returned an empty response.");
    }
}
#endif

#if SUNDER_SKILLS_WORKER
internal sealed class SkillOwnerProvider(
    SkillRuntimeHandler handler,
    SkillRuntimeChangeStream changes) : ISkillsManagementProvider
{
    public async ValueTask<OwnerContent> QueryAsync(
        SunderRpcInvocationContext context,
        OwnerEmpty request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await WriteContentAsync(
                context,
                await handler.HandleAsync(new SkillQuery(), cancellationToken).ConfigureAwait(false),
                "skills-projection.json",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (OwnerJson.ShouldMap(exception))
        {
            throw OwnerJson.Map((SkillOwnerOperationException)exception);
        }
    }

    public async ValueTask<OwnerContent> CommandAsync(
        SunderRpcInvocationContext context,
        OwnerContent request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await ReadContentAsync<SkillCommand>(context, request, cancellationToken)
                .ConfigureAwait(false);
            return await WriteContentAsync(
                context,
                await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false),
                "skills-projection.json",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (OwnerJson.ShouldMap(exception))
        {
            throw OwnerJson.Map((SkillOwnerOperationException)exception);
        }
    }

    public async IAsyncEnumerable<OwnerEmpty> ChangesAsync(
        SunderRpcInvocationContext context,
        OwnerEmpty request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var _ in changes.SubscribeAsync(
                           new SkillChangeSubscription(),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return new OwnerEmpty();
        }
    }

    private static async ValueTask<T> ReadContentAsync<T>(
        SunderRpcInvocationContext context,
        OwnerContent request,
        CancellationToken cancellationToken)
        where T : notnull
    {
        await using var stream = await context.OpenContentAsync(request.Content, cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, OwnerJson.Options, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new SkillOwnerOperationException("The Skills owner request is invalid.");
    }

    private static async ValueTask<OwnerContent> WriteContentAsync<T>(
        SunderRpcInvocationContext context,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, OwnerJson.Options);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await context.RegisterContentAsync(
            stream,
            new SunderRpcContentRegistrationOptions(
                "application/json",
                fileName,
                bytes.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        return new OwnerContent { Content = content };
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

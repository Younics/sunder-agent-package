using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

/// <summary>A private template CLI home, leased through install/list/scaffold.</summary>
internal sealed partial class BuilderTemplateCache : IDisposable
{
    // SDK 10.0.402's GlobalSettings mutex uses a 36-byte GUID plus the escaped
    // CLI home + "/.templateengine/packages.json" (30 bytes). Unix permits 255
    // UTF-8 bytes after removing "Global\\". Reserve another four bytes beyond
    // that 66-byte overhead: never rely on character count or truncate a path.
    internal const int MaximumHomeUtf8Bytes = 185;
    internal const string OwnerName = ".sunder-builder-owner.json";
    internal const string ReadyName = ".sunder-builder-ready.json";
    internal const string LockName = ".sunder-builder-cache.lock";
    private const string Purpose = "Sunder.Builder.DotnetTemplateCache.v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly byte[] owner;
    private readonly byte[] ready;
    private readonly FileStream fileLock;
    private readonly SemaphoreSlim gate;
    private bool disposed;

    internal string HomePath { get; }

    private BuilderTemplateCache(string home, byte[] owner, FileStream fileLock, SemaphoreSlim gate)
    {
        HomePath = home;
        this.owner = owner;
        ready = JsonSerializer.SerializeToUtf8Bytes(new { purpose = Purpose, ownerSha256 = Convert.ToHexStringLower(SHA256.HashData(owner)) });
        this.fileLock = fileLock;
        this.gate = gate;
    }

    internal static string ValidateBasePath(BuilderProjectDriverOptions options)
    {
        var path = options.DotnetTemplateCacheRootPath;
        if (path is null)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile))
                throw new IOException("Builder cannot locate a user profile. Set DotnetTemplateCacheRootPath to a short private absolute directory.");
            path = Path.Combine(profile, ".sunder", "bt");
        }
        path = CanonicalPath(path);
        var bytes = Utf8.GetByteCount(Path.Combine(path, new string('0', 32)));
        if (bytes > MaximumHomeUtf8Bytes)
            throw new IOException($"Builder's .NET template CLI home would be {bytes} UTF-8 bytes; the safe limit is {MaximumHomeUtf8Bytes}. " +
                "Set DotnetTemplateCacheRootPath to a shorter private absolute base directory (the home adds 33 bytes). SDK and NuGet storage need not move.");
        CheckAncestors(path);
        return path;
    }

    internal static async Task<BuilderTemplateCache> AcquireAsync(BuilderProjectDriverOptions options, string sdkVersion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = ValidateBasePath(options);
        var ownershipRoot = CanonicalPath(options.CacheRootPath);
        CheckAncestors(ownershipRoot);
        string? archiveSha256 = null;
        if (options.DotnetTemplatePackagePath is { } package)
        {
            await using var input = File.OpenRead(package);
            if (input.Length > 512L * 1024 * 1024) throw new IOException("Local tooling packages must be existing archives smaller than 512 MiB.");
            archiveSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        }
        // Store the full identity, not just the bounded directory hash. A collision or a
        // copied cache must fail ownership validation before the SDK can mutate anything.
        var owner = JsonSerializer.SerializeToUtf8Bytes(new
        {
            purpose = Purpose, cacheRoot = ownershipRoot, templateBase = root,
            template = "Sunder.Package.Templates@" + options.CoordinatedPackageVersion, archiveSha256, sdkVersion,
        });
        if (owner.Length > MaximumMarkerBytes) throw new IOException("Builder template cache identity exceeds its size limit.");
        var home = Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(owner))[..32]);
        await EnsureOwnedDirectoryAsync(root, JsonSerializer.SerializeToUtf8Bytes(new { purpose = Purpose + ".base", path = root }), token).ConfigureAwait(false);
        await EnsureOwnedDirectoryAsync(home, owner, token).ConfigureAwait(false);
        var gate = Gates.GetOrAdd(home, static _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        FileStream? fileLock = null;
        try
        {
            while (fileLock is null)
            {
                token.ThrowIfCancellationRequested();
                RequireOwner(home, owner);
                var path = Path.Combine(home, LockName);
                CheckStateFile(path, maximumBytes: 0);
                try { fileLock = new FileStream(path, PrivateFileOptions(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                // EAGAIN/EWOULDBLOCK is 11 on Linux and 35 on macOS; Windows
                // reports sharing/lock violations as 32/33. Other IO faults fail fast.
                catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 35)
                { await Task.Delay(100, token).ConfigureAwait(false); }
            }
            token.ThrowIfCancellationRequested();
            RequireOwner(home, owner);
            CheckHomeTree(home);
            return new(home, owner, fileLock, gate);
        }
        catch { fileLock?.Dispose(); gate.Release(); throw; }
    }

    internal bool IsReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireOwner(HomePath, owner);
        var path = Path.Combine(HomePath, ReadyName);
        CheckStateFile(path);
        if (!File.Exists(path)) return false;
        RequireMarker(path, ready);
        return true;
    }

    internal async Task MarkReadyAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireOwner(HomePath, owner);
        CheckHomeTree(HomePath);
        await WriteNewMarkerAsync(Path.Combine(HomePath, ReadyName), ready, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        fileLock.Dispose();
        gate.Release();
    }
}

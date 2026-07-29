using System.Collections.Concurrent;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

internal sealed class ScopedInstructionClaimStore
{
    internal const int CurrentVersion = 2;
    internal const int MaxStoreBytes = 2 * 1024 * 1024;
    internal const int MaxRetainedClaims = 512;
    private const int MaxDocumentsPerClaim = 64;
    private const int MaxTotalDocuments = 4096;
    internal const int MaxPendingProbes = 128;
    private const int MaxPathChars = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();
    private readonly string _rootPath;

    public ScopedInstructionClaimStore(IPackageContext packageContext)
    {
        _rootPath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("files/scoped-instruction-claims");
        Directory.CreateDirectory(_rootPath);
    }

    public async ValueTask<IDisposable> EnterSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionLock(gate);
    }

    public async Task<ScopedInstructionClaimLoadResult> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path))
        {
            return ScopedInstructionClaimLoadResult.Missing;
        }

        try
        {
            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return Quarantine(path);
            }
            if (!TryReadVersion(bytes, out var version))
            {
                return Quarantine(path);
            }
            if (version > CurrentVersion)
            {
                return ScopedInstructionClaimLoadResult.FutureVersion;
            }

            ScopedInstructionClaimState? state;
            try
            {
                state = JsonSerializer.Deserialize<ScopedInstructionClaimState>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                return Quarantine(path);
            }
            return version == CurrentVersion && IsValid(state, sessionId)
                ? new ScopedInstructionClaimLoadResult(ScopedInstructionClaimLoadStatus.Valid, state)
                : Quarantine(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return ScopedInstructionClaimLoadResult.Invalid;
        }
    }

    public async Task SaveAsync(
        ScopedInstructionClaimState state,
        CancellationToken cancellationToken)
    {
        if (!IsValid(state, state.SessionId))
        {
            throw new InvalidOperationException("Scoped instruction claim state is invalid.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length > MaxStoreBytes)
        {
            throw new InvalidOperationException($"Scoped instruction claim state exceeds the {MaxStoreBytes}-byte persistence limit.");
        }

        var path = GetPath(state.SessionId);
        var temporaryPath = Path.Combine(_rootPath, $".{state.SessionId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public void Delete(Guid sessionId)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var path = GetPath(sessionId);
            File.Delete(path);
            File.Delete(path + ".corrupt");
        }
        finally
        {
            gate.Release();
        }
    }

    internal string GetPath(Guid sessionId)
        => Path.Combine(_rootPath, sessionId.ToString("N") + ".json");

    private static bool TryReadVersion(byte[] bytes, out int version)
    {
        version = 0;
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.TryGetInt32(out version);
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxStoreBytes + 1];
        var totalRead = 0;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > MaxStoreBytes)
            {
                return null;
            }
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }
        }

        if (totalRead > MaxStoreBytes)
        {
            return null;
        }
        return buffer[..totalRead];
    }

    private static ScopedInstructionClaimLoadResult Quarantine(string path)
    {
        try
        {
            var quarantinePath = path + ".corrupt";
            File.Move(path, quarantinePath, overwrite: true);
            return ScopedInstructionClaimLoadResult.Rebuild;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScopedInstructionClaimLoadResult.Invalid;
        }
    }

    private static bool IsValid(ScopedInstructionClaimState? state, Guid sessionId)
    {
        if (state is null
            || state.Version != CurrentVersion
            || state.SessionId != sessionId
            || state.TranscriptEpoch <= 0
            || IsBlankOrTooLong(state.WorkspaceId)
            || IsBlankOrTooLong(state.BindingId)
            || IsBlankOrTooLong(state.TargetKind)
            || IsBlankOrTooLong(state.TargetId)
            || !IsHash(state.TargetFingerprint)
            || !IsHash(state.ScopeFingerprint)
             || state.Claims is null
             || state.Claims.Count > MaxRetainedClaims
             || state.Claims.Sum(claim => claim.Documents?.Count ?? 0) > MaxTotalDocuments
             || state.PendingAccessProbes is null
             || state.PendingAccessProbes.Count > MaxPendingProbes)
        {
            return false;
        }

        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in state.Claims)
        {
            if (!directories.Add(claim.Directory)
                || IsBlankOrTooLong(claim.Directory)
                || IsBlankOrTooLong(claim.ScopeRoot)
                || claim.Documents is null
                || claim.Documents.Count > MaxDocumentsPerClaim)
            {
                return false;
            }

            var documentPaths = new HashSet<string>(StringComparer.Ordinal);
            if (claim.Documents.Any(document =>
                    !documentPaths.Add(document.Path)
                    || IsBlankOrTooLong(document.Path)
                    || !IsHash(document.ObservedHash)
                    || (document.PresentedHash is not null && !IsHash(document.PresentedHash))))
            {
                return false;
            }
        }

        var pendingProbes = new HashSet<(string Path, bool IsDirectory, bool FollowFinalSymbolicLink)>();
        if (state.PendingAccessProbes.Any(probe =>
                probe is null
                || IsBlankOrTooLong(probe.Path)
                || !pendingProbes.Add((probe.Path, probe.IsDirectory, probe.FollowFinalSymbolicLink))))
        {
            return false;
        }

        return true;
    }

    private static bool IsBlankOrTooLong(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > MaxPathChars;

    private static bool IsHash(string? value)
        => value is { Length: 64 }
           && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class SessionLock(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
            => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

internal enum ScopedInstructionClaimLoadStatus
{
    Missing,
    Rebuild,
    Valid,
    Invalid,
    FutureVersion,
}

internal sealed record ScopedInstructionClaimLoadResult(
    ScopedInstructionClaimLoadStatus Status,
    ScopedInstructionClaimState? State)
{
    public static ScopedInstructionClaimLoadResult Missing { get; } = new(ScopedInstructionClaimLoadStatus.Missing, null);

    public static ScopedInstructionClaimLoadResult Invalid { get; } = new(ScopedInstructionClaimLoadStatus.Invalid, null);

    public static ScopedInstructionClaimLoadResult Rebuild { get; } = new(ScopedInstructionClaimLoadStatus.Rebuild, null);

    public static ScopedInstructionClaimLoadResult FutureVersion { get; } = new(ScopedInstructionClaimLoadStatus.FutureVersion, null);
}

internal sealed record ScopedInstructionClaimState(
    int Version,
    Guid SessionId,
    string WorkspaceId,
    string BindingId,
    string TargetKind,
    string TargetId,
    string TargetFingerprint,
    string ScopeFingerprint,
    long TranscriptEpoch,
    IReadOnlyList<ScopedInstructionDirectoryClaim> Claims)
{
    public IReadOnlyList<ScopedInstructionPendingProbe> PendingAccessProbes { get; init; } = [];
}

internal sealed record ScopedInstructionDirectoryClaim(
    string Directory,
    string ScopeRoot,
    IReadOnlyList<ScopedInstructionDocumentClaim> Documents);

internal sealed record ScopedInstructionDocumentClaim(
    string Path,
    string ObservedHash,
    string? PresentedHash);

internal sealed record ScopedInstructionPendingProbe(
    string Path,
    bool IsDirectory,
    bool FollowFinalSymbolicLink)
{
    public AgentScopedInstructionProbe ToProbe()
        => new(Path, IsDirectory) { FollowFinalSymbolicLink = FollowFinalSymbolicLink };

    public static ScopedInstructionPendingProbe FromProbe(AgentScopedInstructionProbe probe)
        => new(probe.Path, probe.IsDirectory, probe.FollowFinalSymbolicLink);
}

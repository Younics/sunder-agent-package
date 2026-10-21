using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolchainService
{
    private sealed record CatalogState(string? Recommended = null, DateTimeOffset? LastAttempt = null,
        DateTimeOffset? LastSuccess = null, string? Error = null);
    private sealed record Receipt(int Schema, BuilderToolchainRelease Release, DateTimeOffset InstalledAtUtc);

    private string InstallParent(BuilderProjectKind kind) => Path.Combine(root, "installed", KindName(kind), platform.For(kind));
    private string InstallRoot(BuilderProjectKind kind, string version) => Path.Combine(InstallParent(kind), version);
    private string StatePath(BuilderProjectKind kind, string suffix) => Path.Combine(root, "state", $"{KindName(kind)}-{platform.For(kind)}-{suffix}.json");
    private static string KindName(BuilderProjectKind kind) => kind switch
    {
        BuilderProjectKind.Dotnet => "dotnet-sdk",
        BuilderProjectKind.Node => "node-npm",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private Task<BuilderToolchainLocks.Operation> LockAsync(BuilderProjectKind kind, string version, CancellationToken token)
    {
        if (!BuilderToolchainPolicy.IsSupported(kind, version)) throw new ArgumentException("Unsupported toolchain version.", nameof(version));
        var path = Path.Combine(root, "locks", $"{KindName(kind)}-{platform.For(kind)}-{version}.lock");
        BuilderToolchainFiles.CheckPath(root, path);
        return BuilderToolchainLocks.Operation.AcquireAsync(path, token);
    }

    private void ValidateOwned(BuilderToolchainInstallation installation)
    {
        if (!installation.IsManaged) throw new InvalidOperationException("External toolchains are read-only in Builder. Use their original installer to repair or remove them.");
        if (!BuilderToolchainPolicy.IsSupported(installation.Kind, installation.Version) ||
            !string.Equals(installation.RootPath, InstallRoot(installation.Kind, installation.Version), BuilderToolchainFiles.PathComparison))
            throw new InvalidOperationException("This installation is not owned by this Builder workspace.");
        BuilderToolchainFiles.CheckPath(root, installation.RootPath);
    }

    private BuilderToolchainInstallation InstallationAt(BuilderProjectKind kind, string version, string path, string? npmVersion = null)
    {
        var windows = platform.For(kind).StartsWith("win-", StringComparison.Ordinal);
        return new BuilderToolchainInstallation
        {
            Kind = kind,
            Version = version,
            RootPath = path,
            ExecutablePath = Path.Combine(path, kind == BuilderProjectKind.Dotnet ? windows ? "dotnet.exe" : "dotnet" : windows ? "node.exe" : "bin/node"),
            NpmCliPath = kind == BuilderProjectKind.Node ? Path.Combine(path, windows ? "node_modules/npm/bin/npm-cli.js" : "lib/node_modules/npm/bin/npm-cli.js") : null,
            NpmVersion = npmVersion,
            IsManaged = true,
        };
    }

    private async Task<BuilderToolchainInstallation> ReadManagedAsync(BuilderProjectKind kind, string version, CancellationToken token)
    {
        var path = InstallRoot(kind, version);
        var installation = InstallationAt(kind, version, path);
        try
        {
            BuilderToolchainFiles.CheckPath(root, path);
            var receipt = await BuilderToolchainFiles.ReadJsonAsync<Receipt>(Path.Combine(path, BuilderToolchainFiles.ReceiptName), token).ConfigureAwait(false);
            if (receipt is null || receipt.Schema != 1 || receipt.Release is null || receipt.Release.Kind != kind || receipt.Release.Version != version || receipt.Release.Platform != platform.For(kind))
                return installation with { HealthError = "Installation receipt is missing or invalid. Repair this toolchain." };
            BuilderToolchainPolicy.ValidateRelease(receipt.Release, requireChecksum: true);
            installation = installation with { InstalledAtUtc = receipt.InstalledAtUtc, NpmVersion = receipt.Release.NpmVersion };
            return await VerifyAsync(installation, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return installation with { IsHealthy = false, HealthError = $"Toolchain verification failed: {ex.Message}" };
        }
    }

    private async Task<BuilderToolchainInstallation> InstallAsync(BuilderToolchainRelease release, bool repair,
        IProgress<string>? progress, CancellationToken token)
    {
        BuilderToolchainPolicy.ValidateRelease(release, requireChecksum: false);
        if (release.Platform != platform.For(release.Kind)) throw new InvalidDataException("The catalog returned an archive for another platform.");
        progress?.Report($"Preparing {release.Kind} {release.Version} ({release.Platform}).");
        using var operation = await LockAsync(release.Kind, release.Version, token).ConfigureAwait(false);
        var destination = InstallRoot(release.Kind, release.Version);
        if (!repair && Directory.Exists(destination))
        {
            var current = await ReadManagedAsync(release.Kind, release.Version, token).ConfigureAwait(false);
            if (current.IsHealthy) return current;
        }
        using var mutation = operation.BeginMutation();
        var staging = Path.Combine(root, "staging", $"{KindName(release.Kind)}-{release.Version}-{Guid.NewGuid():N}");
        var payload = Path.Combine(staging, "payload");
        var backup = Path.Combine(staging, "previous");
        var preserveStaging = false;
        var phase = "download";
        BuilderToolchainFiles.CheckPath(root, staging);
        BuilderToolchainFiles.CreatePrivateDirectory(staging);
        try
        {
            release = await catalog.ResolveArchiveAsync(release, token).ConfigureAwait(false);
            BuilderToolchainPolicy.ValidateRelease(release, requireChecksum: true);
            if (release.Platform != platform.For(release.Kind) || destination != InstallRoot(release.Kind, release.Version))
                throw new InvalidDataException("The resolved archive changed its installation identity.");
            var archive = Path.Combine(staging, release.DownloadUri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? "archive.zip" : "archive.tar.gz");
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                await transport.CopyToAsync(release.DownloadUri, output, BuilderToolchainPolicy.MaximumArchiveBytes, progress, token).ConfigureAwait(false);
            phase = "checksum";
            progress?.Report($"Verifying official {release.HashAlgorithm} checksum.");
            if (new FileInfo(archive).Length > BuilderToolchainPolicy.MaximumArchiveBytes) throw new InvalidDataException("The toolchain archive exceeds its size limit.");
            await using (var input = File.OpenRead(archive))
            {
                var actual = release.HashAlgorithm == "SHA512"
                    ? await SHA512.HashDataAsync(input, token).ConfigureAwait(false)
                    : await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(release.Checksum!)))
                    throw new InvalidDataException("The downloaded archive does not match the official checksum. Nothing was installed; retry the download.");
            }
            phase = "extraction";
            progress?.Report("Extracting the verified distribution into private staging.");
            await BuilderToolchainArchive.ExtractAsync(archive, payload, release.ArchiveRoot, token).ConfigureAwait(false);
            phase = "verification";
            progress?.Report(release.Kind == BuilderProjectKind.Dotnet ? "Checking the .NET SDK and SDK resolver." : "Checking Node.js and the bundled npm CLI.");
            var verified = await VerifyAsync(InstallationAt(release.Kind, release.Version, payload, release.NpmVersion), token).ConfigureAwait(false);
            if (!verified.IsHealthy) throw new InvalidOperationException(verified.HealthError);
            var installedAt = clock.GetUtcNow();
            await BuilderToolchainFiles.WriteJsonAsync(Path.Combine(payload, BuilderToolchainFiles.ReceiptName), new Receipt(1, release, installedAt), token).ConfigureAwait(false);
            phase = "promotion";
            BuilderToolchainFiles.CheckPath(root, destination);
            Directory.CreateDirectory(InstallParent(release.Kind));
            token.ThrowIfCancellationRequested();
            var hadPrevious = Directory.Exists(destination);
            if (hadPrevious) Directory.Move(destination, backup);
            try { Directory.Move(payload, destination); }
            catch
            {
                if (hadPrevious)
                {
                    preserveStaging = true;
                    Directory.Move(backup, destination);
                    preserveStaging = false;
                }
                throw;
            }
            // The receipt and fully verified payload become visible together. Do not cancel after commit.
            await RecordOperationAsync(release.Kind, repair ? "repair" : "install", release.Version, "succeeded", null).ConfigureAwait(false);
            progress?.Report($"Ready: {release.Kind} {release.Version} at {destination}.");
            return InstallationAt(release.Kind, release.Version, destination, verified.NpmVersion) with { IsHealthy = true, InstalledAtUtc = installedAt };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await RecordOperationAsync(release.Kind, repair ? "repair" : "install", release.Version, "cancelled", $"Cancelled during {phase}; retry when ready.").ConfigureAwait(false);
            progress?.Report($"Toolchain operation cancelled during {phase}. Staging is being removed; retry when ready.");
            throw;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            var message = $"{release.Kind} {release.Version} failed during {phase}: {ex.Message}";
            await RecordOperationAsync(release.Kind, repair ? "repair" : "install", release.Version, "failed", message).ConfigureAwait(false);
            progress?.Report(message + " Retry or repair from Builder toolchain settings.");
            throw new BuilderToolchainException(phase, message, canRetry: true, ex);
        }
        finally
        {
            if (preserveStaging) progress?.Report($"The previous installation was preserved at {backup}; promotion rollback could not complete.");
            else Cleanup(staging, progress);
        }
    }

    private async Task<(IReadOnlyList<BuilderToolchainRelease>? Releases, CatalogState State)> RefreshCatalogAsync(BuilderProjectKind kind, CancellationToken token)
    {
        var gate = catalogGates.GetOrAdd(kind, static _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = await ReadCatalogStateAsync(kind, token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            CatalogState state;
            IReadOnlyList<BuilderToolchainRelease>? releases = null;
            try
            {
                releases = await catalog.GetReleasesAsync(kind, token).ConfigureAwait(false);
                foreach (var release in releases)
                {
                    BuilderToolchainPolicy.ValidateRelease(release, requireChecksum: false);
                    if (release.Kind != kind || release.Platform != platform.For(kind)) throw new InvalidDataException("The catalog returned the wrong toolchain/platform.");
                }
                var recommended = releases.OrderByDescending(item => Version.Parse(item.Version)).FirstOrDefault()?.Version
                    ?? throw new InvalidDataException("The official catalog contains no supported releases.");
                state = new(recommended, now, now);
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                releases = null;
                state = previous with { LastAttempt = now, Error = $"Official catalog check failed: {ex.Message}" };
            }
            await BuilderToolchainFiles.WriteJsonAsync(StatePath(kind, "catalog"), state, token).ConfigureAwait(false);
            return (releases, state);
        }
        finally { gate.Release(); }
    }

    private async Task<CatalogState> ReadCatalogStateAsync(BuilderProjectKind kind, CancellationToken token)
    {
        try
        {
            var path = StatePath(kind, "catalog");
            BuilderToolchainFiles.CheckPath(root, path);
            return await BuilderToolchainFiles.ReadJsonAsync<CatalogState>(path, token).ConfigureAwait(false) ?? new();
        }
        catch (Exception ex) when (Recoverable(ex)) { return new(Error: $"Saved catalog state is unavailable: {ex.Message}"); }
    }

    private async Task<BuilderToolchainOperation?> ReadOperationAsync(BuilderProjectKind kind, CancellationToken token)
    {
        try
        {
            var path = StatePath(kind, "operation");
            BuilderToolchainFiles.CheckPath(root, path);
            return await BuilderToolchainFiles.ReadJsonAsync<BuilderToolchainOperation>(path, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Recoverable(ex)) { return new("state", "", "unavailable", clock.GetUtcNow(), ex.Message); }
    }

    private async Task RecordOperationAsync(BuilderProjectKind kind, string operation, string version, string outcome, string? error)
    {
        try
        {
            var path = StatePath(kind, "operation");
            BuilderToolchainFiles.CheckPath(root, path);
            await BuilderToolchainFiles.WriteJsonAsync(path, new BuilderToolchainOperation(operation, version, outcome, clock.GetUtcNow(), error), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (Recoverable(ex)) { /* A receipt/promotion result must not be invalidated by a secondary diagnostic write. */ }
    }

    private static void Cleanup(string path, IProgress<string>? progress)
    {
        try { BuilderToolchainFiles.DeleteTree(path); }
        catch (Exception ex) when (Recoverable(ex)) { progress?.Report($"Toolchain temporary-directory cleanup could not complete at {path}: {ex.Message}"); }
    }

    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
        or JsonException or HttpRequestException or TimeoutException or System.ComponentModel.Win32Exception or ArgumentException
        or NotSupportedException or CryptographicException;
}

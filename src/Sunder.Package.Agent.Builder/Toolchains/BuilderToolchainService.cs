using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

/// <summary>Owns local development SDKs, separate from all Host execution-runtime installations.</summary>
public sealed partial class BuilderToolchainService : IDisposable
{
    private readonly string root;
    private readonly IBuilderToolchainCatalog catalog;
    private readonly IBuilderToolchainTransport transport;
    private readonly IBuilderLocalProcessRunner processes;
    private readonly Func<BuilderProjectKind, IReadOnlyList<string>> executableCandidates;
    private readonly TimeProvider clock;
    private readonly BuilderToolchainPlatform platform;
    private readonly SunderDevelopmentToolchain versions;
    private readonly IDisposable? ownedTransport;
    private readonly ConcurrentDictionary<BuilderProjectKind, SemaphoreSlim> catalogGates = new();

    public BuilderToolchainService(IPackageContext context)
        : this(ContextRoot(context), context.DevelopmentToolchain
            ?? throw new InvalidOperationException("This Package Maker requires a Sunder Host that provides its development-toolchain versions.")) { }

    private static string ContextRoot(IPackageContext context)
    {
        var workspace = context.Storage.RoleLocalWorkspace;
        var path = workspace.GetLocalPath("development-toolchains");
        BuilderToolchainFiles.CheckPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.WorkspaceRootPath)), path);
        return path;
    }

    /// <summary>Dependencies are caller-owned. Supply an empty candidate provider to isolate tests from host installations.</summary>
    public BuilderToolchainService(string rootPath, SunderDevelopmentToolchain versions, IBuilderToolchainCatalog? catalog = null,
        IBuilderToolchainTransport? transport = null, IBuilderLocalProcessRunner? processRunner = null,
        Func<BuilderProjectKind, IReadOnlyList<string>>? executableCandidates = null,
        TimeProvider? timeProvider = null, BuilderToolchainPlatform? platform = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        BuilderToolchainPolicy.ValidateVersions(versions);
        this.versions = versions;
        this.platform = platform ?? BuilderToolchainPlatform.Current;
        foreach (var value in new[] { this.platform.DotnetRid, this.platform.NodePlatform })
            if (value.Length is < 3 or > 40 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new ArgumentException("Invalid toolchain platform.", nameof(platform));
        if (transport is null) { var client = new BuilderToolchainHttpTransport(); transport = client; ownedTransport = client; }
        this.transport = transport;
        this.catalog = catalog ?? new BuilderOfficialToolchainCatalog(transport, this.platform, versions);
        processes = processRunner ?? new BuilderLocalProcessRunner();
        this.executableCandidates = executableCandidates ?? DiscoverExecutableCandidates;
        clock = timeProvider ?? TimeProvider.System;
        BuilderToolchainFiles.CreatePrivateDirectory(root);
        foreach (var name in new[] { "installed", "staging", "locks", "state", "cache", "probes" })
        {
            var path = Path.Combine(root, name);
            BuilderToolchainFiles.CheckPath(root, path);
            BuilderToolchainFiles.CreatePrivateDirectory(path);
        }
    }

    public string RootPath => root;
    internal bool IsCurrent(BuilderToolchainInstallation installation)
        => BuilderToolchainPolicy.Matches(versions, installation.Kind, installation.Version, installation.NpmVersion);
    private string RequiredVersion(BuilderProjectKind kind) => kind == BuilderProjectKind.Dotnet ? versions.DotnetSdkVersion : versions.NodeVersion;

    public async Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installed = await ListInstalledAsync(kind, cancellationToken).ConfigureAwait(false);
        var state = checkUpdates
            ? (await RefreshCatalogAsync(kind, cancellationToken).ConfigureAwait(false)).State
            : await ReadCatalogStateAsync(kind, cancellationToken).ConfigureAwait(false);
        var fresh = state.LastSuccess is { } success && state.Error is null && clock.GetUtcNow() >= success && clock.GetUtcNow() - success < BuilderToolchainPolicy.CatalogFreshness;
        var latestInstalled = installed.Where(item => item.IsHealthy && IsCurrent(item))
            .Select(item => Version.Parse(item.Version)).OrderDescending().FirstOrDefault();
        var update = fresh && BuilderToolchainPolicy.TryVersion(state.Recommended, out var recommended)
            ? latestInstalled is null || latestInstalled < recommended : (bool?)null;
        return new(kind, installed, RequiredVersion(kind), state.LastAttempt, update, state.Error)
        {
            LastSuccessfulCheckAtUtc = state.LastSuccess,
            LastOperation = await ReadOperationAsync(kind, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requirements = BuilderToolchainRequirements.Read(kind, projectDirectory, versions);
        var installed = (await ListInstalledAsync(kind, cancellationToken).ConfigureAwait(false)).ToList();
        if (kind == BuilderProjectKind.Dotnet && requirements.DotnetExecutables.Count > 0)
            installed.AddRange(await DiscoverAsync(kind, requirements.DotnetExecutables, cancellationToken).ConfigureAwait(false));
        var usable = requirements.Order(installed.Where(item => item.IsHealthy && IsCurrent(item) && requirements.Allows(kind, item.Version, item.NpmVersion)),
            static item => item.Version).ToArray();
        if (projectDirectory is not null)
        {
            foreach (var candidate in usable)
                if (await MatchesProjectAsync(candidate, projectDirectory, cancellationToken).ConfigureAwait(false))
                    return await FinishSelectionAsync(candidate, progress, cancellationToken).ConfigureAwait(false);
        }
        else if (usable.FirstOrDefault() is { } ready)
            return await FinishSelectionAsync(ready, progress, cancellationToken).ConfigureAwait(false);
        var (releases, state) = await RefreshCatalogAsync(kind, cancellationToken).ConfigureAwait(false);
        if (releases is null)
        {
            // Offline use is explicit, and the failed catalog state remains visible to settings.
            if (projectDirectory is null && usable.FirstOrDefault() is { } offline)
            {
                progress?.Report($"Update check unavailable; using installed {kind} {offline.Version}. {state.Error}");
                return offline;
            }
            throw new BuilderToolchainException("catalog", $"No usable installed toolchain satisfies {requirements.Description}. {state.Error} Retry the catalog check when online.", true);
        }
        var release = requirements.Order(releases.Where(item => item.Version == RequiredVersion(kind)
                && requirements.Allows(kind, item.Version, kind == BuilderProjectKind.Node ? versions.NpmVersion : null)),
            static item => item.Version).FirstOrDefault()
            ?? throw new BuilderToolchainException("selection", $"No supported official {kind} SDK/distribution satisfies {requirements.Description}. Existing project requirements were preserved.", false);
        foreach (var candidate in usable.Where(item => item.Version == release.Version))
            if (await MatchesProjectAsync(candidate, projectDirectory, cancellationToken).ConfigureAwait(false))
                return await FinishSelectionAsync(candidate, progress, cancellationToken).ConfigureAwait(false);
        var result = await InstallAsync(release, repair: false, progress, cancellationToken).ConfigureAwait(false);
        if (!await MatchesProjectAsync(result, projectDirectory, cancellationToken).ConfigureAwait(false))
            throw new BuilderToolchainException("selection", $"Installed {kind} {result.Version}, but {requirements.Description} selects a different SDK or excludes this installation path. Check the project requirements (including global.json sdk.paths).", false);
        return await FinishSelectionAsync(result, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BuilderToolchainSelection> EnsureSelectedAsync(BuilderProjectKind kind, string? projectDirectory = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var installation = await EnsureAsync(kind, projectDirectory, progress, cancellationToken).ConfigureAwait(false);
        var selection = await AcquireLeaseAsync(installation, cancellationToken).ConfigureAwait(false);
        try
        {
            var requirements = BuilderToolchainRequirements.Read(kind, projectDirectory, versions);
            if (!requirements.Allows(kind, selection.Installation.Version, selection.Installation.NpmVersion) ||
                !await MatchesProjectAsync(selection.Installation, projectDirectory, cancellationToken).ConfigureAwait(false))
                throw new BuilderToolchainException("selection", "The project requirements or toolchain changed during selection. Retry the operation.", true);
            return selection;
        }
        catch { selection.Dispose(); throw; }
    }

    public async Task<BuilderToolchainSelection> AcquireLeaseAsync(BuilderToolchainInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action? release = null;
        try
        {
            if (installation.IsManaged)
            {
                ValidateOwned(installation);
                using var operation = await LockAsync(installation.Kind, installation.Version, cancellationToken).ConfigureAwait(false);
                release = operation.BeginSelection();
                installation = await ReadManagedAsync(installation.Kind, installation.Version, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (BuilderToolchainFiles.IsChild(installation.ExecutablePath, root) || BuilderToolchainFiles.IsChild(installation.RootPath, root))
                    throw new InvalidOperationException("A private toolchain cannot be selected as an external installation.");
                installation = await VerifyAsync(installation, cancellationToken).ConfigureAwait(false);
            }
            if (!IsCurrent(installation)) throw new BuilderToolchainException("selection", "This toolchain was superseded by the running Sunder release. Install its current SDK.", false);
            if (!installation.IsHealthy) throw new BuilderToolchainException("verification", installation.HealthError ?? "The toolchain is unhealthy; repair it before use.", true);
            return new(installation, CreateEnvironment(installation), release);
        }
        catch { release?.Invoke(); throw; }
    }

    public async Task<IReadOnlyList<BuilderToolchainInstallation>> ListInstalledAsync(BuilderProjectKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<BuilderToolchainInstallation>();
        var parent = InstallParent(kind);
        BuilderToolchainFiles.CheckPath(root, parent);
        if (Directory.Exists(parent))
        {
            foreach (var directory in Directory.EnumerateDirectories(parent).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = Path.GetFileName(directory);
                if (!BuilderToolchainPolicy.IsKnownVersion(kind, version)) continue;
                using var operation = await LockAsync(kind, version, cancellationToken).ConfigureAwait(false);
                if (!Directory.Exists(directory)) continue;
                var release = operation.BeginSelection();
                try { result.Add(await ReadManagedAsync(kind, version, cancellationToken).ConfigureAwait(false)); }
                finally { release(); }
            }
        }
        result.AddRange(await DiscoverAsync(kind, executableCandidates(kind), cancellationToken).ConfigureAwait(false));
        return result.DistinctBy(item => (item.Version, item.ExecutablePath))
            .OrderByDescending(item => Version.Parse(item.Version)).ThenByDescending(item => item.IsManaged).ToArray();
    }

    public async Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwned(installation);
        if (!IsCurrent(installation)) return await EnsureAsync(installation.Kind, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        using (var operation = await LockAsync(installation.Kind, installation.Version, cancellationToken).ConfigureAwait(false))
        using (operation.BeginMutation()) { }
        var (releases, state) = await RefreshCatalogAsync(installation.Kind, cancellationToken).ConfigureAwait(false);
        var release = releases?.SingleOrDefault(item => item.Version == installation.Version)
            ?? throw new BuilderToolchainException("catalog", $"Cannot repair {installation.Version} from the official catalog. {state.Error}", true);
        return await InstallAsync(release, repair: true, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwned(installation);
        using var operation = await LockAsync(installation.Kind, installation.Version, cancellationToken).ConfigureAwait(false);
        using var mutation = operation.BeginMutation();
        BuilderToolchainFiles.CheckPath(root, installation.RootPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(installation.RootPath)) return;
        // Commit removal by rename. Cancellation never leaves a half-deleted ready installation.
        var trash = Path.Combine(root, "staging", "removed-" + Guid.NewGuid().ToString("N"));
        Directory.Move(installation.RootPath, trash);
        Cleanup(trash, progress);
        await RecordOperationAsync(installation.Kind, "remove", installation.Version, "succeeded", null).ConfigureAwait(false);
        progress?.Report($"Removed managed {installation.Kind} {installation.Version}.");
    }

    public void Dispose() => ownedTransport?.Dispose();
}

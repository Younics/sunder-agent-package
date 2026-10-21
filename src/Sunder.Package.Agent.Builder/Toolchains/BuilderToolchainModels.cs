namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderToolchainInstallation
{
    public required BuilderProjectKind Kind { get; init; }
    public required string Version { get; init; }
    public required string ExecutablePath { get; init; }
    public required string RootPath { get; init; }
    public string? NpmCliPath { get; init; }
    public string? NpmVersion { get; init; }
    public bool IsManaged { get; init; }
    public bool IsHealthy { get; init; }
    public string? HealthError { get; init; }
    public DateTimeOffset? InstalledAtUtc { get; init; }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderToolchainStatus(
    BuilderProjectKind Kind,
    IReadOnlyList<BuilderToolchainInstallation> Installed,
    string? RecommendedVersion,
    DateTimeOffset? LastCheckedAtUtc,
    bool? UpdateAvailable,
    string? CatalogError)
{
    public DateTimeOffset? LastSuccessfulCheckAtUtc { get; init; }
    public BuilderToolchainOperation? LastOperation { get; init; }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderToolchainOperation(
    string Operation, string Version, string Outcome, DateTimeOffset AtUtc, string? Error);

/// <summary>Dispose only after the selected process, including watch and child processes, has stopped.</summary>
public sealed class BuilderToolchainSelection : IDisposable, IAsyncDisposable
{
    private Action? release;
    private int disposed;

    internal BuilderToolchainSelection(BuilderToolchainInstallation installation,
        IReadOnlyDictionary<string, string?> environment, Action? release)
    {
        Installation = installation;
        Environment = environment;
        this.release = release;
    }

    public BuilderToolchainInstallation Installation { get; }
    public IReadOnlyDictionary<string, string?> Environment { get; }

    public BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory,
        bool npm = false, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (npm && Installation.NpmCliPath is null)
            throw new InvalidOperationException("The selected toolchain does not contain npm.");
        return new BuilderLocalProcessRequest(Installation.ExecutablePath,
            npm ? [Installation.NpmCliPath!, .. arguments] : arguments, workingDirectory)
        {
            Environment = Environment,
            Timeout = timeout ?? TimeSpan.FromMinutes(10),
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Exchange(ref release, null)?.Invoke();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public sealed record BuilderToolchainRelease(
    BuilderProjectKind Kind, string Version, string Platform, Uri DownloadUri,
    string HashAlgorithm, string? Checksum, string? ArchiveRoot, string? NpmVersion = null);

public interface IBuilderToolchainCatalog
{
    Task<IReadOnlyList<BuilderToolchainRelease>> GetReleasesAsync(BuilderProjectKind kind, CancellationToken cancellationToken);
    Task<BuilderToolchainRelease> ResolveArchiveAsync(BuilderToolchainRelease release, CancellationToken cancellationToken);
}

/// <summary>Trusted injected transport; the production implementation restricts every request and redirect.</summary>
public interface IBuilderToolchainTransport
{
    Task CopyToAsync(Uri uri, Stream destination, long maximumBytes, IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public sealed class BuilderToolchainException(string phase, string message, bool canRetry, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Phase { get; } = phase;
    public bool CanRetry { get; } = canRetry;
}

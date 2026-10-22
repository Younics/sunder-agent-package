using System.Reflection;
using System.Text.RegularExpressions;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed record BuilderProjectDriverOptions(string CacheRootPath)
{
    /// <summary>Stable workspace exclusion, independent of retained feed and template caches.</summary>
    public string ProjectMutationLockRootPath { get; init; } = Path.Combine(CacheRootPath, "project-locks");
    public string CoordinatedPackageVersion { get; init; } = SdkVersion();
    public string? DotnetTemplatePackagePath { get; init; }
    /// <summary>
    /// Optional short, private base for Builder-owned .NET template CLI homes. Defaults to
    /// &lt;user profile&gt;/.sunder/bt; independent of SDK installs and the normal NuGet/tools cache.
    /// Each home adds a separator and a 32-character identity hash to this base.
    /// </summary>
    public string? DotnetTemplateCacheRootPath { get; init; }
    public string? NuGetConfigPath { get; init; }
    public string? NodeCreatorPackagePath { get; init; }
    public IReadOnlyDictionary<string, string> NodePackageOverrides { get; init; } = new Dictionary<string, string>();
    public string? NodeRegistryUri { get; init; }
    public string? NodeMetadataToolCliPath { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(15);

    private static string SdkVersion()
    {
        var version = typeof(IPackageContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        return version is not null && Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? version : "1.1.0";
    }
}

public interface IBuilderProjectDriverSelection : IAsyncDisposable
{
    BuilderToolchainInstallation Installation { get; }
    BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory, bool npm = false);
}

public interface IBuilderProjectDriverToolchains
{
    Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken);
    Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory, IProgress<string>? progress, CancellationToken cancellationToken);
    Task<IBuilderProjectDriverSelection> SelectAsync(BuilderProjectKind kind, string? projectDirectory,
        BuilderToolchainInstallation? preferred, IProgress<string>? progress, CancellationToken cancellationToken);
    Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken);
    Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed class BuilderProjectDriverToolchains(BuilderToolchainService service) : IBuilderProjectDriverToolchains
{
    public Task<BuilderToolchainStatus> GetStatusAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken)
        => service.GetStatusAsync(kind, checkUpdates, cancellationToken);
    public Task<BuilderToolchainInstallation> EnsureAsync(BuilderProjectKind kind, string? projectDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
        => service.EnsureAsync(kind, projectDirectory, progress, cancellationToken);
    public async Task<IBuilderProjectDriverSelection> SelectAsync(BuilderProjectKind kind, string? projectDirectory,
        BuilderToolchainInstallation? preferred, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (preferred is not null && (preferred.Kind != kind || !BuilderToolchainPolicy.IsSupported(kind, preferred.Version)))
            throw new InvalidOperationException("The prepared toolchain does not match this project.");
        var selection = preferred is not null && projectDirectory is null
            ? await service.AcquireLeaseAsync(preferred, cancellationToken).ConfigureAwait(false)
            : await service.EnsureSelectedAsync(kind, projectDirectory, progress, cancellationToken).ConfigureAwait(false);
        return new Selection(selection);
    }
    public Task<BuilderToolchainInstallation> RepairAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
        => service.RepairAsync(installation, progress, cancellationToken);
    public Task RemoveAsync(BuilderToolchainInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken)
        => service.RemoveAsync(installation, progress, cancellationToken);

    private sealed class Selection(BuilderToolchainSelection selection) : IBuilderProjectDriverSelection
    {
        public BuilderToolchainInstallation Installation => selection.Installation;
        public BuilderLocalProcessRequest CreateProcess(IReadOnlyList<string> arguments, string workingDirectory, bool npm = false)
            => selection.CreateProcess(arguments, workingDirectory, npm);
        public ValueTask DisposeAsync() => selection.DisposeAsync();
    }
}

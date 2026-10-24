using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

public static class BuilderToolchainPolicy
{
    public const string DotnetChannel = "10.0";
    public const string MinimumDotnetSdkVersion = "10.0.100";
    public const string MinimumNodeVersion = "24.18.1";
    public const int NodeMajorVersion = 24;
    public const long MaximumMetadataBytes = 8 * 1024 * 1024;
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 6L * 1024 * 1024 * 1024;
    public const int MaximumArchiveEntries = 150_000;
    public static TimeSpan CatalogFreshness => TimeSpan.FromHours(24);
    public static Uri DotnetMetadataUri => new($"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{DotnetChannel}/releases.json");
    public static Uri NodeMetadataUri => new("https://nodejs.org/dist/index.json");

    public static bool IsSupported(BuilderProjectKind kind, string version)
    {
        if (!TryVersion(version, out var parsed)) return false;
        return kind switch
        {
            BuilderProjectKind.Dotnet => parsed.Major == 10 && parsed.Minor == 0 && parsed >= Version.Parse(MinimumDotnetSdkVersion),
            BuilderProjectKind.Node => parsed.Major == NodeMajorVersion && parsed >= Version.Parse(MinimumNodeVersion),
            _ => false,
        };
    }

    internal static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        return text is not null && Regex.IsMatch(text, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) && Version.TryParse(text, out version!);
    }

    internal static void ValidateRelease(BuilderToolchainRelease release, bool requireChecksum)
    {
        if (release is null || release.DownloadUri is null || string.IsNullOrEmpty(release.Platform))
            throw new InvalidDataException("The toolchain archive identity is incomplete.");
        if (!IsSupported(release.Kind, release.Version)) throw new InvalidDataException("The archive is outside the supported stable toolchain policy.");
        BuilderToolchainHttpTransport.ValidateUri(release.DownloadUri);
        var suffix = release.Platform.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var expectedName = release.Kind == BuilderProjectKind.Dotnet
            ? $"dotnet-sdk-{release.Version}-{release.Platform}{suffix}"
            : $"node-v{release.Version}-{release.Platform}{suffix}";
        var expectedPath = release.Kind == BuilderProjectKind.Dotnet
            ? $"/dotnet/Sdk/{release.Version}/{expectedName}"
            : $"/dist/v{release.Version}/{expectedName}";
        if (release.DownloadUri.AbsolutePath != expectedPath ||
            (release.Kind == BuilderProjectKind.Node) != (release.DownloadUri.Host == "nodejs.org") ||
            release.ArchiveRoot != (release.Kind == BuilderProjectKind.Node ? expectedName[..^suffix.Length] : null))
            throw new InvalidDataException("The catalog archive does not match its SDK/distribution identity.");
        var algorithm = release.Kind == BuilderProjectKind.Dotnet ? "SHA512" : "SHA256";
        if (release.HashAlgorithm != algorithm || (requireChecksum &&
            (release.Checksum is null || release.Checksum.Length != (algorithm == "SHA512" ? 128 : 64) || !release.Checksum.All(Uri.IsHexDigit))))
            throw new InvalidDataException("The official archive checksum is missing or malformed.");
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderToolchainPlatform(string DotnetRid, string NodePlatform)
{
    public static BuilderToolchainPlatform Current
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => throw new PlatformNotSupportedException("Builder toolchains require an x64 or arm64 process."),
            };
            if (OperatingSystem.IsWindows()) return new($"win-{architecture}", $"win-{architecture}");
            if (OperatingSystem.IsMacOS()) return new($"osx-{architecture}", $"darwin-{architecture}");
            if (OperatingSystem.IsLinux())
            {
                var musl = RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal) || File.Exists("/etc/alpine-release");
                return new(musl ? $"linux-musl-{architecture}" : $"linux-{architecture}",
                    musl ? $"linux-{architecture}-musl" : $"linux-{architecture}");
            }
            throw new PlatformNotSupportedException("Builder toolchains support Windows, macOS and Linux.");
        }
    }

    public string For(BuilderProjectKind kind) => kind switch
    {
        BuilderProjectKind.Dotnet => DotnetRid,
        BuilderProjectKind.Node => NodePlatform,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

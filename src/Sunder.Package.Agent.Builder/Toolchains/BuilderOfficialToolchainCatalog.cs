using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderOfficialToolchainCatalog(IBuilderToolchainTransport transport, BuilderToolchainPlatform platform, SunderDevelopmentToolchain versions)
    : IBuilderToolchainCatalog
{
    public async Task<IReadOnlyList<BuilderToolchainRelease>> GetReleasesAsync(BuilderProjectKind kind, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(kind == BuilderProjectKind.Dotnet ? BuilderToolchainPolicy.DotnetMetadataUri(versions) : BuilderToolchainPolicy.NodeMetadataUri,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var releases = kind switch
        {
            BuilderProjectKind.Dotnet => ReadDotnet(document.RootElement),
            BuilderProjectKind.Node => ReadNode(document.RootElement),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (releases.Count == 0) throw new InvalidDataException($"No supported stable {kind} SDK/distribution for {platform.For(kind)} is available in the official catalog.");
        return releases.OrderByDescending(static item => Version.Parse(item.Version)).ToArray();
    }

    public async Task<BuilderToolchainRelease> ResolveArchiveAsync(BuilderToolchainRelease release, CancellationToken cancellationToken)
    {
        BuilderToolchainPolicy.ValidateRelease(release, requireChecksum: false);
        if (release.Kind == BuilderProjectKind.Node)
        {
            var bytes = await ReadAsync(new Uri($"https://nodejs.org/dist/v{release.Version}/SHASUMS256.txt"), cancellationToken).ConfigureAwait(false);
            var filename = Path.GetFileName(release.DownloadUri.AbsolutePath);
            var matches = Encoding.UTF8.GetString(bytes).Split('\n')
                .Select(static line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == filename).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The Node distribution has no unique official SHA256 checksum.");
            release = release with { Checksum = matches[0][0] };
        }
        BuilderToolchainPolicy.ValidateRelease(release, requireChecksum: true);
        return release;
    }

    private List<BuilderToolchainRelease> ReadDotnet(JsonElement root)
    {
        if (Text(root, "channel-version") != $"{Version.Parse(versions.DotnetSdkVersion).Major}.0" || Text(root, "release-type") is not ("lts" or "sts") ||
            Text(root, "support-phase") is not ("active" or "maintenance"))
            throw new InvalidDataException("The .NET channel is not supported.");
        var rid = platform.DotnetRid;
        var suffix = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var result = new Dictionary<string, BuilderToolchainRelease>(StringComparer.Ordinal);
        foreach (var release in root.GetProperty("releases").EnumerateArray())
        {
            // Only SDK records are considered. Runtime-only installations cannot satisfy Builder.
            var sdks = release.TryGetProperty("sdks", out var many)
                ? many.EnumerateArray().ToArray()
                : release.TryGetProperty("sdk", out var one) ? [one] : Array.Empty<JsonElement>();
            foreach (var sdk in sdks)
            {
                var version = Text(sdk, "version");
                if (version != versions.DotnetSdkVersion) continue;
                var expectedName = $"dotnet-sdk-{rid}{suffix}";
                foreach (var file in sdk.GetProperty("files").EnumerateArray())
                {
                    if (Text(file, "rid") != rid || Text(file, "name") != expectedName) continue;
                    var candidate = new BuilderToolchainRelease(BuilderProjectKind.Dotnet, version, rid,
                        new Uri(Text(file, "url")), "SHA512", Text(file, "hash"), null);
                    BuilderToolchainPolicy.ValidateRelease(candidate, requireChecksum: true);
                    if (result.TryGetValue(version, out var previous) && previous != candidate)
                        throw new InvalidDataException("The .NET catalog contains conflicting SDK records.");
                    result[version] = candidate;
                }
            }
        }
        return result.Values.ToList();
    }

    private List<BuilderToolchainRelease> ReadNode(JsonElement root)
    {
        var target = platform.NodePlatform;
        var suffix = target.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var catalogPlatform = target.StartsWith("darwin-", StringComparison.Ordinal)
            ? target.Replace("darwin-", "osx-", StringComparison.Ordinal) + "-tar"
            : target.StartsWith("win-", StringComparison.Ordinal) ? target + "-zip" : target;
        var result = new List<BuilderToolchainRelease>();
        foreach (var item in root.EnumerateArray())
        {
            var version = Text(item, "version").TrimStart('v');
            if (version != versions.NodeVersion ||
                !item.TryGetProperty("lts", out var lts) || lts.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(lts.GetString()) ||
                !BuilderToolchainPolicy.TryVersion(Text(item, "npm"), out _)) continue;
            if (!item.GetProperty("files").EnumerateArray().Any(file => file.GetString() == catalogPlatform)) continue;
            var archiveRoot = $"node-v{version}-{target}";
            var candidate = new BuilderToolchainRelease(BuilderProjectKind.Node, version, target,
                 new Uri($"https://nodejs.org/dist/v{version}/{archiveRoot}{suffix}"), "SHA256", null, archiveRoot, Text(item, "npm"));
            BuilderToolchainPolicy.ValidateRelease(candidate, requireChecksum: false);
            if (result.Any(previous => previous.Version == version)) throw new InvalidDataException("The Node catalog contains duplicate versions.");
            result.Add(candidate);
        }
        return result;
    }

    private async Task<byte[]> ReadAsync(Uri uri, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await transport.CopyToAsync(uri, buffer, BuilderToolchainPolicy.MaximumMetadataBytes, null, token).ConfigureAwait(false);
        if (buffer.Length > BuilderToolchainPolicy.MaximumMetadataBytes) throw new InvalidDataException("Toolchain metadata exceeds the size limit.");
        return buffer.ToArray();
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}

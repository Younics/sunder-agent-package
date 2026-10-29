using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolchainService
{
    private async Task InstallNpmAsync(string payload, string staging, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report($"Installing Sunder's exact npm {versions.NpmVersion}.");
        using var metadata = new MemoryStream();
        await transport.CopyToAsync(new($"https://registry.npmjs.org/npm/{versions.NpmVersion}"), metadata,
            BuilderToolchainPolicy.MaximumMetadataBytes, null, token).ConfigureAwait(false);
        if (metadata.Length > BuilderToolchainPolicy.MaximumMetadataBytes) throw new InvalidDataException("npm metadata exceeds its limit.");
        using var document = JsonDocument.Parse(metadata.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var value = document.RootElement;
        var uri = new Uri($"https://registry.npmjs.org/npm/-/npm-{versions.NpmVersion}.tgz");
        var distribution = value.GetProperty("dist");
        var integrity = distribution.GetProperty("integrity").GetString() ?? "";
        if (value.GetProperty("name").GetString() != "npm" || value.GetProperty("version").GetString() != versions.NpmVersion
            || distribution.GetProperty("tarball").GetString() != uri.AbsoluteUri || !integrity.StartsWith("sha512-", StringComparison.Ordinal))
            throw new InvalidDataException("npm metadata does not describe the selected official npm archive.");
        var checksum = Convert.FromBase64String(integrity[7..]);
        if (checksum.Length != 64) throw new InvalidDataException("npm requires a SHA512 integrity checksum.");
        var archive = Path.Combine(staging, "npm.tar.gz");
        await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await transport.CopyToAsync(uri, output, BuilderToolchainPolicy.MaximumArchiveBytes, progress, token).ConfigureAwait(false);
        await using (var input = File.OpenRead(archive))
        {
            var actual = await SHA512.HashDataAsync(input, token).ConfigureAwait(false);
            if (input.Length > BuilderToolchainPolicy.MaximumArchiveBytes
                || !CryptographicOperations.FixedTimeEquals(checksum, actual))
                throw new InvalidDataException("The npm archive failed its official SHA512 integrity check.");
        }
        var extracted = Path.Combine(staging, "npm");
        await BuilderToolchainArchive.ExtractAsync(archive, extracted, "package", token).ConfigureAwait(false);
        var destination = Path.GetDirectoryName(Path.GetDirectoryName(InstallationAt(BuilderProjectKind.Node, versions.NodeVersion, payload).NpmCliPath!)!)!;
        BuilderToolchainFiles.CheckPath(root, destination);
        BuilderToolchainFiles.DeleteTree(destination); // Only the unpromoted Node staging payload.
        Directory.Move(extracted, destination);
    }
}

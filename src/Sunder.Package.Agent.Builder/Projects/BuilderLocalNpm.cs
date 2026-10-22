using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

/// <summary>Retains one complete, optional local npm selection per job, without changing user npm configuration.</summary>
internal static class BuilderLocalNpm
{
    internal const string ConfigurationName = "local-npm.json";
    private const string OwnerName = ".sunder-builder-local-npm.json";
    private const int MaximumStateBytes = 16 * 1024;
    private const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private static readonly string[] PackageIds = ["@sunder/sdk", "@sunder/package-tool", "create-sunder-package"];

    internal static async Task<BuilderProjectDriverOptions> LoadOptionsAsync(BuilderProjectDriverOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = BuilderJobValidation.Absolute(options.CacheRootPath, "Builder tools cache");
        var pointer = Path.Combine(root, ConfigurationName);
        byte[] bytes;
        try { bytes = await ReadSmallAsync(pointer, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return options; }
        catch (DirectoryNotFoundException) { return options; }
        try
        {
            using var json = BuilderProjectSource.Json(bytes);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
                !value.TryGetProperty("schemaVersion", out var schema) || schema.GetRawText() != "1" ||
                !value.TryGetProperty("feedPath", out var feed) || feed.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Expected exactly { \"schemaVersion\": 1, \"feedPath\": \"<absolute local folder>\" }.");
            var source = BuilderJobValidation.Absolute(feed.GetString()!, "Local npm feed");
            if (source.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("The npm override requires a local folder, not a network share.");
            CheckPath(source);
            return await RetainAsync(options, root, source, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Builder {pointer}: {error.Message} The configured local npm feed must contain all three coordinated Sunder packages; public Sunder fallback is disabled.", error);
        }
    }

    private static async Task<BuilderProjectDriverOptions> RetainAsync(BuilderProjectDriverOptions options, string root, string source, CancellationToken token)
    {
        var version = options.CoordinatedPackageVersion;
        if (version.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(version,
            @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidDataException("An exact coordinated Sunder package version is required.");
        var files = Directory.EnumerateFileSystemEntries(source).Take(4097).ToArray();
        if (files.Length > 4096) throw new InvalidDataException("Local npm feed exceeds 4,096 top-level entries.");
        var selected = PackageIds.Select(id =>
        {
            var name = id.TrimStart('@').Replace('/', '-') + "-" + version + ".tgz";
            var matches = files.Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Local npm feed requires exactly one flat {name} archive (found {matches.Length}).");
            return (Id: id, Name: name, Path: matches[0]);
        }).ToArray();
        var container = Path.Combine(root, "local-npm");
        CheckPath(container);
        BuilderToolchainFiles.CreatePrivateDirectory(container);
        var stage = Path.Combine(container, ".stage-" + Guid.NewGuid().ToString("N"));
        BuilderToolchainFiles.CreatePrivateDirectory(stage);
        try
        {
            var packages = new List<PackageBytes>();
            foreach (var item in selected)
            {
                var destination = Path.Combine(stage, item.Name);
                packages.Add(await CopyAsync(item.Path, destination, token).ConfigureAwait(false));
                await ValidateAsync(destination, item.Id, version, token).ConfigureAwait(false);
            }
            var owner = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, feedPath = source, version, packages });
            var retained = Path.Combine(container, Convert.ToHexStringLower(SHA256.HashData(owner)));
            await using (var output = OpenNew(Path.Combine(stage, OwnerName)))
            {
                await output.WriteAsync(owner, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            CheckPath(retained);
            try { Directory.Move(stage, retained); }
            catch (IOException) when (Directory.Exists(retained)) { /* Verify the whole immutable selection; never repair it. */ }
            if (!(await ReadSmallAsync(Path.Combine(retained, OwnerName), token).ConfigureAwait(false)).AsSpan().SequenceEqual(owner) ||
                !Directory.EnumerateFileSystemEntries(retained).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                    .SequenceEqual(packages.Select(item => item.Name).Append(OwnerName).Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Retained local npm selection has changed. Existing cache was preserved.");
            foreach (var package in packages)
            {
                await using var input = OpenRead(Path.Combine(retained, package.Name));
                if (input.Length != package.Length || await HashAsync(input, token).ConfigureAwait(false) != package.Sha256)
                    throw new InvalidDataException($"Retained local npm {package.Name} bytes have changed. Existing cache was preserved.");
            }
            return options with
            {
                NodeCreatorPackagePath = Path.Combine(retained, selected[2].Name),
                NodePackageOverrides = new Dictionary<string, string>
                {
                    [PackageIds[0]] = Path.Combine(retained, selected[0].Name),
                    [PackageIds[1]] = Path.Combine(retained, selected[1].Name),
                },
            };
        }
        finally
        {
            if (Directory.Exists(stage)) { CheckPath(stage); BuilderToolchainFiles.DeleteTree(stage); }
        }
    }

    private static async Task ValidateAsync(string path, string id, string version, CancellationToken token)
    {
        await using var input = OpenRead(path);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? manifest = null;
        long expanded = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false) is { } entry)
        {
            var name = entry.Name;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                !name.StartsWith("package/", StringComparison.Ordinal) || name.Length > 1024 || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/').Any(part => part is "" or "." or "..") || !entries.Add(name) || entries.Count > 8192 ||
                entry.Length > MaximumArchiveBytes || (expanded += entry.Length) > 256L * 1024 * 1024)
                throw new InvalidDataException($"{id} contains unsafe, duplicate, or oversized archive entries.");
            var data = entry.DataStream ?? Stream.Null;
            if (name == "package/package.json")
            {
                if (entry.Length > 64 * 1024) throw new InvalidDataException($"{id} package.json exceeds 64 KiB.");
                manifest = new byte[checked((int)entry.Length)];
                await data.ReadExactlyAsync(manifest, token).ConfigureAwait(false);
            }
            else await data.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
        }
        using var json = BuilderProjectSource.Json(manifest ?? throw new InvalidDataException($"{id} is missing package.json."));
        var value = json.RootElement;
        if (value.GetProperty("name").GetString() != id || value.GetProperty("version").GetString() != version)
            throw new InvalidDataException($"Local npm package identity must be exactly {id}@{version}.");
        var required = id switch
        {
            "@sunder/sdk" => new[] { "dist/index.js", "dist/index.d.ts", "dist/generated.js", "dist/browser.js", "dist/react.js" },
            "@sunder/package-tool" => ["dist/cli.js", "dist/index.js", "dist/index.d.ts"],
            _ => ["dist/cli.js", "template/package.json", "template-react-node/package.json"],
        };
        if (required.Any(name => !entries.Contains("package/" + name))) throw new InvalidDataException($"{id} is missing a required package artifact.");
        if (id != "@sunder/sdk")
        {
            var constraint = value.GetProperty("dependencies").GetProperty("@sunder/sdk").GetString();
            if (!Version.TryParse(version.Split('-')[0], out var parsed) || parsed.Minor == int.MaxValue)
                throw new InvalidDataException("The coordinated npm version is outside the supported version range.");
            if (constraint != version && constraint != $">={parsed.Major}.{parsed.Minor}.0 <{parsed.Major}.{parsed.Minor + 1}.0")
                throw new InvalidDataException($"{id} must depend on the coordinated local Sunder SDK.");
        }
    }

    private sealed record PackageBytes(string Name, long Length, string Sha256);

    private static async Task<PackageBytes> CopyAsync(string source, string destination, CancellationToken token)
    {
        await using var input = OpenRead(source);
        var length = input.Length;
        var stamp = File.GetLastWriteTimeUtc(source);
        if (length is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("Local npm archives must be nonempty and at most 64 MiB each.");
        await using (var output = OpenNew(destination))
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if ((copied += read) > length) throw new IOException("Local npm archive changed while being copied.");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (copied != length) throw new IOException("Local npm archive was truncated while being copied.");
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        CheckPath(source);
        if (input.Length != length || new FileInfo(source).Length != length || File.GetLastWriteTimeUtc(source) != stamp)
            throw new IOException("Local npm archive changed during selection. Retry with a complete, stable feed.");
        await using var retained = OpenRead(destination);
        return new(Path.GetFileName(destination), length, await HashAsync(retained, token).ConfigureAwait(false));
    }

    private static async Task<string> HashAsync(Stream input, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if ((length += read) > MaximumArchiveBytes) throw new InvalidDataException("Local npm archive exceeds 64 MiB.");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<byte[]> ReadSmallAsync(string path, CancellationToken token)
    {
        await using var input = OpenRead(path);
        if (input.Length > MaximumStateBytes) throw new InvalidDataException("Local npm metadata exceeds 16 KiB.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new IOException("Local npm metadata changed during selection.");
        return bytes;
    }

    private static void CheckPath(string path) => BuilderProjectSource.EnsureSafePath(path, path);
    private static FileStream OpenRead(string path)
    {
        CheckPath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    }
    private static FileStream OpenNew(string path)
    {
        CheckPath(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }
}

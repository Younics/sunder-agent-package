using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Sunder.Package.Agent.Builder;

/// <summary>Optional, instance-local source-pack override. No installed SDK or global NuGet state is changed.</summary>
internal static partial class BuilderLocalNuGet
{
    internal const string ConfigurationName = "local-nuget.json";
    private const string OwnerName = ".sunder-builder-local-nuget.json";
    private const string SourcePolicy = "Sunder.Builder.LocalNuGet.v1;local=Sunder.*;nugetorg=*:https://api.nuget.org/v3/index.json;fallback=none";
    private const int MaximumStateBytes = 16 * 1024;
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumTotalBytes = 1024L * 1024 * 1024;
    private static readonly string[] PackageIds =
        ["Sunder.Package.Build", "Sunder.Package.Templates", "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker"];

    /// <summary>Diagnostic entry point and production job resolution. A missing pointer returns the original options.</summary>
    internal static async Task<BuilderProjectDriverOptions> LoadOptionsAsync(BuilderProjectDriverOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = BuilderJobValidation.Absolute(options.CacheRootPath, "Builder tools cache");
        var pointer = Path.Combine(root, ConfigurationName);
        CheckPath(pointer);
        byte[] bytes;
        try { bytes = await ReadSmallAsync(pointer, MaximumStateBytes, token).ConfigureAwait(false); }
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
            var source = BuilderJobValidation.Absolute(feed.GetString()!, "Local NuGet feed");
            if (source.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("The NuGet override requires a local folder, not a network share.");
            CheckPath(source);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Local NuGet feed is missing: {source}");
            return await RetainAsync(options, root, source, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or
            UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Builder {pointer}: {error.Message} The configured local Sunder feed must be complete; public Sunder fallback is disabled.", error);
        }
    }

    private static async Task<BuilderProjectDriverOptions> RetainAsync(BuilderProjectDriverOptions options, string root, string source, CancellationToken token)
    {
        var version = options.CoordinatedPackageVersion;
        if (version.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(version,
            @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidDataException("An exact coordinated Sunder package version is required.");
        // Only these six flat, exact-coordinate filenames are considered. No recursive search,
        // newest-version selection, or fallback to another directory/source is permitted.
        var files = Directory.EnumerateFileSystemEntries(source).Take(4097).ToArray();
        if (files.Length > 4096) throw new InvalidDataException("Local NuGet feed exceeds 4,096 top-level entries.");
        var selected = PackageIds.Order(StringComparer.Ordinal).Select(id =>
        {
            var name = id + "." + version + ".nupkg";
            var matches = files.Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Local NuGet feed requires exactly one flat {name} archive (found {matches.Length}).");
            return (Id: id, Name: name, Path: matches[0]);
        }).ToArray();
        var container = Path.Combine(root, "local-nuget");
        CheckPath(container);
        BuilderToolchainFiles.CreatePrivateDirectory(container);
        var stage = Path.Combine(container, ".stage-" + Guid.NewGuid().ToString("N"));
        BuilderToolchainFiles.CreatePrivateDirectory(stage);
        try
        {
            var stagedFeed = Path.Combine(stage, "feed");
            BuilderToolchainFiles.CreatePrivateDirectory(stagedFeed);
            var packages = new List<PackageBytes>();
            long total = 0;
            foreach (var item in selected)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(stagedFeed, item.Name);
                var package = await CopyPackageAsync(item.Path, destination, MaximumTotalBytes - total, token).ConfigureAwait(false);
                if ((total += package.Length) > MaximumTotalBytes) throw new InvalidDataException("The six local NuGet archives exceed 1 GiB.");
                await ValidatePackageAsync(destination, item.Id, version, token).ConfigureAwait(false);
                packages.Add(package);
            }
            var identity = JsonSerializer.SerializeToUtf8Bytes(new { policy = SourcePolicy, version, feedPath = source, packages });
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(identity));
            var retained = Path.Combine(container, fingerprint);
            var owner = JsonSerializer.SerializeToUtf8Bytes(new { cacheRoot = root, fingerprint, identity = Convert.ToBase64String(identity) });
            var config = NuGetConfig(Path.Combine(retained, "feed"));
            await WriteNewAsync(Path.Combine(stage, OwnerName), owner, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(stage, "NuGet.Config"), config, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CheckPath(retained);
            try { Directory.Move(stage, retained); }
            catch (IOException) when (Directory.Exists(retained)) { /* A retained selection must match in full, never be overwritten. */ }
            await VerifyRetainedAsync(retained, owner, config, packages, token).ConfigureAwait(false);
            var tools = Path.Combine(retained, "tools");
            foreach (var cache in new[] { "nuget-packages", "nuget-http", "nuget-plugins", "nuget-scratch" }) CheckPath(Path.Combine(tools, cache));
            return options with
            {
                CacheRootPath = tools,
                DotnetTemplatePackagePath = Path.Combine(retained, "feed", "Sunder.Package.Templates." + version + ".nupkg"),
                NuGetConfigPath = Path.Combine(retained, "NuGet.Config"),
            };
        }
        finally
        {
            // Only this unpublished, random staging tree; never repair/delete an existing cache.
            if (Directory.Exists(stage))
            {
                CheckPath(stage);
                BuilderToolchainFiles.DeleteTree(stage);
            }
        }
    }

    private static byte[] NuGetConfig(string feed) => Encoding.UTF8.GetBytes(new XDocument(new XElement("configuration",
        new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "local"), new XAttribute("value", feed)),
            new XElement("add", new XAttribute("key", "nugetorg"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
        new XElement("packageSourceMapping", new XElement("clear"),
            new XElement("packageSource", new XAttribute("key", "local"), new XElement("package", new XAttribute("pattern", "Sunder.*"))),
            new XElement("packageSource", new XAttribute("key", "nugetorg"), new XElement("package", new XAttribute("pattern", "*")))),
        new XElement("fallbackPackageFolders", new XElement("clear")))).ToString() + "\n");

    private static async Task VerifyRetainedAsync(string directory, byte[] owner, byte[] config, IReadOnlyList<PackageBytes> packages, CancellationToken token)
    {
        CheckPath(directory);
        foreach (var (name, expected) in new[] { (OwnerName, owner), ("NuGet.Config", config) })
            if (!(await ReadSmallAsync(Path.Combine(directory, name), MaximumStateBytes, token).ConfigureAwait(false)).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"Retained local NuGet {name} does not match its source identity. Existing cache was preserved.");
        var feed = Path.Combine(directory, "feed");
        CheckPath(feed);
        var files = Directory.EnumerateFileSystemEntries(feed).Take(7).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!files.SequenceEqual(packages.Select(package => package.Name))) throw new InvalidDataException("Retained local NuGet feed has unexpected or missing files.");
        foreach (var package in packages)
        {
            var path = Path.Combine(feed, package.Name);
            CheckPath(path);
            await using var input = OpenRead(path);
            if (input.Length != package.Length || !string.Equals(await HashBoundedAsync(input, token).ConfigureAwait(false), package.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Retained local NuGet {package.Name} bytes do not match. Existing cache was preserved.");
            CheckPath(path);
        }
    }

    private sealed record PackageBytes(string Name, long Length, string Sha256);

    private static async Task<PackageBytes> CopyPackageAsync(string source, string destination, long remaining, CancellationToken token)
    {
        CheckPath(source);
        var stamp = File.GetLastWriteTimeUtc(source);
        await using var input = OpenRead(source);
        var length = input.Length;
        if (length is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("Local NuGet archives must be nonempty and at most 512 MiB each.");
        if (length > remaining) throw new InvalidDataException("The six local NuGet archives exceed 1 GiB.");
        await using (var output = OpenNew(destination))
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if ((copied += read) > length) throw new IOException("Local NuGet archive changed while being copied.");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (copied != length) throw new IOException("Local NuGet archive was truncated while being copied.");
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        CheckPath(source);
        if (input.Length != length || new FileInfo(source).Length != length || File.GetLastWriteTimeUtc(source) != stamp)
            throw new IOException("Local NuGet archive changed during selection. Retry with a complete, stable feed.");
        await using var retained = OpenRead(destination);
        return new(Path.GetFileName(destination), length, await HashBoundedAsync(retained, token).ConfigureAwait(false));
    }

    private static async Task<string> HashBoundedAsync(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long size = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if ((size += count) > MaximumArchiveBytes) throw new InvalidDataException("Local NuGet archive exceeds 512 MiB.");
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void CheckPath(string path) => BuilderProjectSource.EnsureSafePath(path, path);
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    private static FileStream OpenNew(string path)
    {
        CheckPath(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }

    private static async Task<byte[]> ReadSmallAsync(string path, int maximum, CancellationToken token)
    {
        CheckPath(path);
        await using var stream = OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException($"Local NuGet metadata exceeds its {maximum}-byte limit: {path}");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new IOException("Local NuGet metadata changed during selection.");
        CheckPath(path);
        return bytes;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("Local NuGet state exceeds 16 KiB.");
        await using var stream = OpenNew(path);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}

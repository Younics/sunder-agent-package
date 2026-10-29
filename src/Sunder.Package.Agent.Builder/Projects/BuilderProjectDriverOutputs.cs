using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class BuilderProjectDriverOutputs
{
    private const int MaximumMetadata = 1024 * 1024;
    private const string Manifest = "manifest/sunder-package.json";
    private const string Index = "manifest/content-index.json";

    internal static string DevelopmentFolder(BuilderProjectInspection project, string configuration)
    {
        var root = project.Kind == BuilderProjectKind.Dotnet ? Path.Combine(project.ProjectFolder, "bin", configuration, DotnetFramework(project)) : NodeOutput(project);
        var path = Path.Combine(root, "sunder-dev");
        BuilderProjectFiles.EnsureSafePath(project.ProjectFolder, path);
        return path;
    }

    internal static string ArchivePath(BuilderProjectInspection project)
    {
        if (project.Kind == BuilderProjectKind.Dotnet) return Path.Combine(project.ProjectFolder, "bin", "Release", DotnetFramework(project), project.PackageId + ".sunderpkg");
        using var config = ReadJson(project.EntryPoint);
        var version = config.RootElement.GetProperty("version").GetString() ?? "";
        if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) || version.Length > 128)
            throw new InvalidDataException("The package version is not a canonical archive version.");
        var path = Path.Combine(NodeOutput(project), project.PackageId + "." + version + ".sunderpkg");
        BuilderProjectFiles.EnsureSafePath(project.ProjectFolder, path);
        return path;
    }

    internal static void RequireScript(string root, string script)
    {
        using var json = ReadJson(Path.Combine(root, "package.json"));
        if (!json.RootElement.TryGetProperty("scripts", out var scripts) || !scripts.TryGetProperty(script, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"The project has no '{script}' script. Update its coordinated Sunder tooling/scripts before retrying.");
    }

    internal static async Task<string> ValidateDevelopmentAsync(string path, BuilderProjectInspection project, string rid, bool requireApp, CancellationToken token)
    {
        BuilderProjectFiles.EnsureSafePath(project.ProjectFolder, path);
        var manifestPath = Contained(path, Manifest);
        var manifestBytes = ReadMetadata(manifestPath);
        using var manifest = BuilderProjectFiles.Json(manifestBytes);
        var targets = ValidateManifest(manifest.RootElement, project, rid, requireApp);
        var indexPath = Contained(path, Index);
        var indexBytes = ReadMetadata(indexPath);
        using var index = BuilderProjectFiles.Json(indexBytes);
        var entries = ValidateIndex(index.RootElement);
        if (!entries.ContainsKey(Manifest)) throw new InvalidDataException("The development output index omits its manifest.");
        _ = ResolveEntryPoints(targets, entries.Keys);
        long total = 0;
        foreach (var (name, evidence) in entries)
        {
            token.ThrowIfCancellationRequested();
            var file = Contained(path, name);
            var info = new FileInfo(file);
            if (!info.Exists || info.Length != evidence.Size || (total += evidence.Size) > 1024L * 1024 * 1024)
                throw new InvalidDataException("Development output has a missing, oversized or changed indexed file.");
            await using var stream = File.OpenRead(file);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!string.Equals(digest, evidence.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Development output failed its content-index checksum check. Rebuild the project.");
        }
        if (!manifestBytes.AsSpan().SequenceEqual(ReadMetadata(manifestPath)) || !indexBytes.AsSpan().SequenceEqual(ReadMetadata(indexPath)))
            throw new InvalidDataException("Development metadata changed during output validation.");
        // Canonical V1 full-source identity: SHA256(manifest SHA256 || content-index SHA256).
        // The Host independently validates the entire selected output again before staging it.
        return Convert.ToHexStringLower(SHA256.HashData([.. SHA256.HashData(manifestBytes), .. SHA256.HashData(indexBytes)]));
    }

    internal static async Task ValidateArchiveAsync(string archivePath, BuilderProjectInspection project, string rid, CancellationToken token)
    {
        BuilderProjectFiles.EnsureSafePath(project.ProjectFolder, archivePath);
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length > 1024L * 1024 * 1024) throw new InvalidDataException("The package command did not produce a bounded canonical archive.");
        await using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096) throw new InvalidDataException("The package archive has too many entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            _ = Contained(project.ProjectFolder, entry.FullName);
            if (!names.Add(entry.FullName)) throw new InvalidDataException("The package archive has duplicate or case-colliding entries.");
        }
        var manifestBytes = await ReadEntryAsync(archive.GetEntry(Manifest), token).ConfigureAwait(false);
        using var manifest = BuilderProjectFiles.Json(manifestBytes);
        var targets = ValidateManifest(manifest.RootElement, project, rid, requireApp: false);
        using var index = BuilderProjectFiles.Json(await ReadEntryAsync(archive.GetEntry(Index), token).ConfigureAwait(false));
        var evidence = ValidateIndex(index.RootElement);
        var entryPoints = ResolveEntryPoints(targets, evidence.Keys);
        if (!evidence.TryGetValue(Manifest, out var manifestEvidence) || manifestEvidence.Size != manifestBytes.Length ||
            !string.Equals(manifestEvidence.Hash, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), StringComparison.OrdinalIgnoreCase) ||
            entryPoints.Any(target => archive.GetEntry(target) is null))
            throw new InvalidDataException("The package archive is missing its indexed manifest or entry points.");
    }

    private sealed record OutputTarget(string Role, string Rid, string EntryPoint);

    private static OutputTarget[] ValidateManifest(JsonElement manifest, BuilderProjectInspection project, string rid, bool requireApp)
    {
        if (manifest.GetProperty("archiveFormatVersion").GetInt32() != 1 || manifest.GetProperty("manifestVersion").GetInt32() != 1 ||
            manifest.GetProperty("id").GetString() != project.PackageId || string.IsNullOrEmpty(manifest.GetProperty("version").GetString()))
            throw new InvalidDataException("Output manifest does not describe the selected canonical Sunder package.");
        var targets = manifest.GetProperty("targets").EnumerateArray().ToArray();
        if (project.Kind == BuilderProjectKind.Dotnet && project.TargetFramework is { } framework
            && targets.Where(target => target.GetProperty("role").GetString() == "runtime").Any(target =>
                !target.TryGetProperty("targetFramework", out var actual) || actual.GetString() != framework))
            throw new InvalidDataException("The built Worker target framework differs from the evaluated project. Inspect and rebuild the current source.");
        if (targets.Length is < 1 or > 32 || !targets.Any(target => target.GetProperty("rid").GetString() == rid &&
            target.GetProperty("role").GetString() == "runtime" && target.GetProperty("kind").GetString() == "worker") ||
            requireApp && !targets.Any(target => target.GetProperty("rid").GetString() == rid && target.GetProperty("role").GetString() == "app"))
            throw new InvalidDataException($"Output manifest has no complete current-RID ({rid}) development target.");
        var result = targets.Select(target => new OutputTarget(target.GetProperty("role").GetString() ?? "",
            target.GetProperty("rid").GetString() ?? "", target.GetProperty("entryPoint").GetString() ?? "")).ToArray();
        var keys = new HashSet<(string, string)>();
        foreach (var target in result)
        {
            if (target.Role is not ("app" or "runtime") || target.Rid.Length is < 1 or > 40 ||
                target.Rid.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') || !keys.Add((target.Role, target.Rid)))
                throw new InvalidDataException("Output manifest contains an invalid or duplicate target key.");
            ValidateRelativePath(target.EntryPoint);
        }
        return result;
    }

    private static string[] ResolveEntryPoints(IEnumerable<OutputTarget> targets, IEnumerable<string> paths)
    {
        var indexed = paths.ToHashSet(StringComparer.Ordinal);
        return targets.Select(target =>
        {
            // Entry points are logical paths in the canonical v1 payload union, not
            // archive-root paths. Layers may supply a path once; they never override it.
            string[] candidates = [$"payload/shared/{target.EntryPoint}", $"payload/{target.Role}/shared/{target.EntryPoint}",
                $"payload/{target.Role}/{target.Rid}/{target.EntryPoint}"];
            var matches = candidates.Where(indexed.Contains).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException(matches.Length == 0
                ? $"Output entry point '{target.EntryPoint}' does not resolve in target '{target.Role}/{target.Rid}' payload layers."
                : $"Output entry point '{target.EntryPoint}' is ambiguous across target '{target.Role}/{target.Rid}' payload layers.");
        }).ToArray();
    }

    private static Dictionary<string, (long Size, string Hash)> ValidateIndex(JsonElement index)
    {
        if (index.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported output content index.");
        var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.GetProperty("files").EnumerateArray())
        {
            var name = entry.GetProperty("path").GetString() ?? "";
            var size = entry.GetProperty("size").GetInt64();
            var hash = entry.GetProperty("sha256").GetString() ?? "";
            if (result.Count >= 4096 || !names.Add(name) || size is < 0 or > 256L * 1024 * 1024 || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("The output content index is malformed or exceeds its limits.");
            result.Add(name, (size, hash));
        }
        return result;
    }

    private static string NodeOutput(BuilderProjectInspection project)
    {
        using var json = ReadJson(project.EntryPoint);
        var directory = json.RootElement.TryGetProperty("outputDirectory", out var output) ? output.GetString() ?? "" : "dist";
        return Contained(project.ProjectFolder, directory);
    }

    private static string DotnetFramework(BuilderProjectInspection project)
    {
        if (project.TargetFramework is { } evaluated) return evaluated;
        var xml = System.Xml.Linq.XDocument.Load(project.EntryPoint);
        var frameworks = xml.Descendants("TargetFramework").Select(element => element.Value.Trim()).Distinct().ToArray();
        if (frameworks.Length == 1 && Regex.IsMatch(frameworks[0], @"^net[1-9][0-9]\.0$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            return frameworks[0];
        throw new InvalidDataException("The project's target framework must be evaluated before locating its build output.");
    }

    internal static IReadOnlyList<string> BuiltOutputs(BuilderProjectInspection project)
    {
        if (project.Kind == BuilderProjectKind.Node) return [DevelopmentFolder(project, "Debug")];
        var outputs = new List<string>();
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var root = Path.Combine(project.ProjectFolder, "bin", configuration);
            BuilderProjectFiles.EnsureSafePath(project.ProjectFolder, root);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).Take(32))
            {
                BuilderProjectFiles.EnsureSafePath(project.ProjectFolder, directory);
                if (Regex.IsMatch(Path.GetFileName(directory), @"^net[1-9][0-9]\.0$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    outputs.Add(Path.Combine(directory, "sunder-dev"));
            }
        }
        return outputs;
    }

    private static string Contained(string root, string relative)
    {
        ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        BuilderProjectFiles.EnsureSafePath(root, path);
        return path;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (relative.Length is < 1 or > 1024 || relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl) ||
            Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("An output path is not a canonical contained file path.");
    }

    private static JsonDocument ReadJson(string path)
        => BuilderProjectFiles.Json(ReadMetadata(path));

    private static byte[] ReadMetadata(string path)
    {
        BuilderProjectFiles.EnsureSafePath(Path.GetDirectoryName(path)!, path);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumMetadata) throw new InvalidDataException("Required source/output metadata is missing or exceeds 1 MiB: " + path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumMetadata) throw new InvalidDataException("Required source/output metadata grew beyond its byte limit.");
        return bytes;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry? entry, CancellationToken token)
    {
        if (entry is null || entry.Length is < 1 or > MaximumMetadata) throw new InvalidDataException("Archive metadata is missing or oversized.");
        await using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException("Archive metadata exceeds its declared length.");
        return bytes;
    }
}

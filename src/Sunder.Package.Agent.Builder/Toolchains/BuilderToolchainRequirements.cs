using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

internal sealed class BuilderToolchainRequirements
{
    private string? projectRoot;
    private IReadOnlyDictionary<string, byte[]>? proposedFiles;
    private readonly List<Func<Version, bool>> node = [];
    private readonly List<Func<Version, bool>> npm = [];
    private Version? sdk;
    private string rollForward = "patch";
    internal List<string> DotnetExecutables { get; } = [];
    internal string Description { get; private set; } = "Sunder's exact development-toolchain versions";

    internal static BuilderToolchainRequirements Read(BuilderProjectKind kind, string? projectDirectory,
        Sunder.Sdk.Abstractions.SunderDevelopmentToolchain? versions = null, IReadOnlyDictionary<string, byte[]>? proposedFiles = null)
    {
        var result = new BuilderToolchainRequirements();
        if (projectDirectory is null) return result;
        var directory = Path.GetFullPath(projectDirectory);
        result.projectRoot = directory;
        result.proposedFiles = proposedFiles;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Project directory does not exist: {directory}");
        if (kind == BuilderProjectKind.Dotnet) result.ReadDotnet(directory);
        else if (kind == BuilderProjectKind.Node) result.ReadNode(directory);
        else throw new ArgumentOutOfRangeException(nameof(kind));
        if (versions is not null && !result.Allows(kind, kind == BuilderProjectKind.Dotnet ? versions.DotnetSdkVersion : versions.NodeVersion, versions.NpmVersion))
            throw new BuilderToolchainException("selection", $"{result.Description} excludes Sunder's current toolchain ({versions.DotnetSdkVersion}, Node {versions.NodeVersion}, npm {versions.NpmVersion}). Update the project's development pins; its target framework was not changed.", false);
        return result;
    }

    internal bool Allows(BuilderProjectKind kind, string version, string? npmVersion)
    {
        if (!BuilderToolchainPolicy.IsKnownVersion(kind, version)) return false;
        var parsed = Version.Parse(version);
        if (kind == BuilderProjectKind.Node)
            return node.All(test => test(parsed)) && (npm.Count == 0 ||
                (BuilderToolchainPolicy.TryVersion(npmVersion, out var npmParsed) && npm.All(test => test(npmParsed))));
        if (sdk is null) return true;
        if (parsed < sdk) return false;
        return rollForward switch
        {
            "disable" => parsed == sdk,
            "patch" or "latestPatch" => parsed.Major == sdk.Major && parsed.Minor == sdk.Minor && parsed.Build / 100 == sdk.Build / 100,
            "feature" or "latestFeature" => parsed.Major == sdk.Major && parsed.Minor == sdk.Minor,
            "minor" or "latestMinor" => parsed.Major == sdk.Major,
            "major" or "latestMajor" => true,
            _ => false,
        };
    }

    internal IEnumerable<T> Order<T>(IEnumerable<T> candidates, Func<T, string> version)
    {
        if (sdk is null || rollForward.StartsWith("latest", StringComparison.Ordinal))
            return candidates.OrderByDescending(item => Version.Parse(version(item)));
        return candidates.OrderBy(item => rollForward == "patch" && Version.Parse(version(item)) == sdk ? 0 : 1)
            .ThenBy(item => Version.Parse(version(item)).Major)
            .ThenBy(item => Version.Parse(version(item)).Minor)
            .ThenBy(item => Version.Parse(version(item)).Build / 100)
            .ThenByDescending(item => Version.Parse(version(item)).Build);
    }

    private void ReadDotnet(string directory)
    {
        var path = Find(directory, "global.json");
        if (path is null) return;
        using var document = JsonDocument.Parse(ReadSmall(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32,
        });
        Description = path;
        if (!document.RootElement.TryGetProperty("sdk", out var settings)) return;
        if (settings.TryGetProperty("version", out var version))
        {
            var text = version.GetString();
            if (text is null || !BuilderToolchainPolicy.TryVersion(text, out _))
                throw new InvalidOperationException($"{path} requests an invalid SDK version; the existing pin was not changed.");
            sdk = Version.Parse(text);
        }
        if (settings.TryGetProperty("rollForward", out var roll))
        {
            rollForward = roll.GetString() ?? "";
            if (rollForward is not ("patch" or "feature" or "minor" or "major" or "latestPatch" or "latestFeature" or "latestMinor" or "latestMajor" or "disable") || sdk is null)
                throw new InvalidDataException($"Unsupported or incomplete SDK rollForward policy in {path}.");
        }
        if (settings.TryGetProperty("paths", out var paths))
        {
            foreach (var item in paths.EnumerateArray())
            {
                var value = item.GetString() ?? throw new InvalidDataException("global.json sdk.paths contains a non-string path.");
                if (value == "$host$") continue;
                DotnetExecutables.Add(Path.Combine(Path.GetFullPath(value, Path.GetDirectoryName(path)!), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            }
        }
    }

    private void ReadNode(string directory)
    {
        var sources = new List<string>();
        foreach (var filename in new[] { ".nvmrc", ".node-version", ".tool-versions" })
        {
            var path = Find(directory, filename);
            if (path is null) continue;
            var text = ReadSmall(path);
            if (filename == ".tool-versions")
            {
                string[][] ReadLines(string contents) => contents.Split('\n')
                    .Select(line => line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length > 0 && parts[0] == "nodejs").ToArray();
                var lines = ReadLines(text);
                while (lines.Length == 0 && Directory.GetParent(Path.GetDirectoryName(path)!) is { } parent)
                {
                    var ancestor = Find(parent.FullName, filename);
                    if (ancestor is null) break;
                    path = ancestor;
                    lines = ReadLines(ReadSmall(path));
                }
                if (lines.Length == 0) continue;
                if (lines.Length != 1 || lines[0].Length != 2) throw new InvalidDataException($"{path} must specify one Node.js version.");
                text = lines[0][1];
            }
            else text = text.Split('#')[0].Trim();
            if (text.Length == 0) throw new InvalidDataException($"The Node version pin is empty: {path}");
            if (text.StartsWith("lts/", StringComparison.Ordinal))
                throw new InvalidDataException($"{path} uses a floating LTS alias. Replace it with Sunder's exact Node version.");
            node.Add(BuilderToolchainVersionRange.Parse(text));
            sources.Add(path);
        }
        var packagePath = Find(directory, "package.json");
        if (packagePath is not null)
        {
            using var document = JsonDocument.Parse(ReadSmall(packagePath), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.TryGetProperty("engines", out var engines))
            {
                AddRange(engines, "node", node);
                AddRange(engines, "npm", npm);
            }
            if (root.TryGetProperty("volta", out var volta))
            {
                if (volta.TryGetProperty("extends", out _))
                    throw new InvalidDataException($"Inherited Volta configuration in {packagePath} must be resolved to explicit node/npm pins before Builder can select it.");
                AddRange(volta, "node", node);
                AddRange(volta, "npm", npm);
            }
            if (root.TryGetProperty("packageManager", out var manager) && manager.GetString() is { } managerText)
            {
                if (!managerText.StartsWith("npm@", StringComparison.Ordinal))
                    throw new InvalidOperationException($"{packagePath} pins a package manager other than npm. Builder's Node toolchain includes npm.");
                npm.Add(BuilderToolchainVersionRange.Parse(managerText[4..].Split('+')[0]));
            }
            if (root.TryGetProperty("devEngines", out var devEngines))
            {
                ReadDevEngine(devEngines, "runtime", "node", node);
                ReadDevEngine(devEngines, "packageManager", "npm", npm);
            }
            sources.Add(packagePath);
        }
        if (sources.Count > 0) Description = string.Join(", ", sources);
    }

    private static void ReadDevEngine(JsonElement engines, string key, string expectedName, List<Func<Version, bool>> target)
    {
        if (!engines.TryGetProperty(key, out var engine)) return;
        // Array alternatives carry npm-specific onFail semantics; do not silently reinterpret them.
        if (engine.ValueKind != JsonValueKind.Object ||
            (engine.TryGetProperty("name", out var name) && name.GetString() != expectedName))
            throw new InvalidDataException($"Unsupported devEngines.{key} requirement. Use a single {expectedName} version requirement.");
        AddRange(engine, "version", target);
    }

    private static void AddRange(JsonElement source, string property, List<Func<Version, bool>> target)
    {
        if (source.TryGetProperty(property, out var value))
            target.Add(BuilderToolchainVersionRange.Parse(value.GetString() ?? throw new InvalidDataException($"Invalid {property} version constraint.")));
    }

    private static string? Find(string directory, string name)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private string ReadSmall(string path)
    {
        if (projectRoot is not null && proposedFiles?.TryGetValue(Path.GetRelativePath(projectRoot, path), out var bytes) == true)
        {
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException($"Toolchain requirements file is too large: {path}");
            return new BuilderSourceFile(path, bytes).Text;
        }
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException($"Toolchain requirements file is too large: {path}");
        return File.ReadAllText(path);
    }
}

/// <summary>Stable semver ranges used by Node pins/engines; unsupported syntax fails closed.</summary>
internal static class BuilderToolchainVersionRange
{
    internal static Func<Version, bool> Parse(string text)
    {
        if (text.Length > 1024) throw new InvalidDataException("The Node version constraint is too long.");
        var alternatives = text.Trim().Split("||", StringSplitOptions.TrimEntries).Select(ParseSet).ToArray();
        return version => alternatives.Any(test => test(version));
    }

    private static Func<Version, bool> ParseSet(string text)
    {
        if (text.Length == 0 || text == "*") return _ => true;
        var hyphen = text.Split(" - ", StringSplitOptions.TrimEntries);
        if (hyphen.Length == 2)
        {
            var (lower, _) = Partial(hyphen[0]);
            var (upper, parts) = Partial(hyphen[1]);
            return value => value >= lower && (parts == 3 ? value <= upper : value < Next(upper, parts));
        }
        var matches = Regex.Matches(text, @"(>=|<=|>|<|=|\^|~)?\s*(v?(?:[0-9]+|[xX*])(?:\.(?:[0-9]+|[xX*])){0,2})",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var tests = new List<Func<Version, bool>>();
        var end = 0;
        foreach (Match match in matches)
        {
            if (!string.IsNullOrWhiteSpace(text[end..match.Index])) throw Invalid(text);
            // Adjacent comparators are legal; adjacent bare versions are not.
            if (end != 0 && match.Index == end && match.Groups[1].Length == 0) throw Invalid(text);
            end = match.Index + match.Length;
            var (version, parts) = Partial(match.Groups[2].Value);
            var op = match.Groups[1].Value;
            tests.Add(op switch
            {
                "" or "=" => value => parts == 0 || (parts == 3 ? value == version : value >= version && value < Next(version, parts)),
                ">=" => value => value >= version,
                ">" => value => parts == 3 ? value > version : value >= Next(version, parts),
                "<" => value => value < version,
                "<=" => value => parts == 3 ? value <= version : value < Next(version, parts),
                "~" => value => parts == 0 || value >= version && value < Next(version, parts <= 1 ? 1 : 2),
                "^" => value => parts == 0 || value >= version && value < Next(version,
                    version.Major > 0 || parts == 1 ? 1 : version.Minor > 0 || parts == 2 ? 2 : 3),
                _ => throw Invalid(text),
            });
        }
        if (tests.Count == 0 || !string.IsNullOrWhiteSpace(text[end..])) throw Invalid(text);
        return value => tests.All(test => test(value));
    }

    private static (Version Version, int Parts) Partial(string text)
    {
        var segments = text.TrimStart('v').Split('.');
        if (segments.Length is < 1 or > 3) throw Invalid(text);
        var values = new int[3];
        var count = 0;
        var wildcard = false;
        foreach (var segment in segments)
        {
            if (segment is "x" or "X" or "*") { wildcard = true; continue; }
            if (wildcard || segment.Length == 0 || segment.Length > 6 || (segment.Length > 1 && segment[0] == '0') ||
                !segment.All(char.IsAsciiDigit) || !int.TryParse(segment, out values[count])) throw Invalid(text);
            count++;
        }
        return (new(values[0], values[1], values[2]), count);
    }

    private static Version Next(Version version, int parts) => parts switch
    {
        0 => new Version(int.MaxValue, 0, 0),
        1 => new Version(version.Major + 1, 0, 0),
        2 => new Version(version.Major, version.Minor + 1, 0),
        _ => new Version(version.Major, version.Minor, version.Build + 1),
    };

    private static InvalidDataException Invalid(string text) => new($"Unsupported Node/npm version requirement '{text}'. Use a stable semver version or range; the project pin was not changed.");
}

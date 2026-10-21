using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Sunder.Package.Agent.Builder;

// Notification paths only. MSBuild owns restore/build evaluation and byte-level authority.
internal sealed record BuilderDotnetBuildInputs(IReadOnlyList<string> Roots, IReadOnlyList<string> Files)
{
    internal static async Task<BuilderDotnetBuildInputs> DiscoverAsync(BuilderProjectInspection project,
        IReadOnlyList<string> toolingRoots, string? config, CancellationToken token)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal) { project.ProjectFolder };
        var files = new HashSet<string>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.Ordinal) { project.EntryPoint };
        var imports = new Queue<(string Path, string ProjectDirectory)>();
        var visited = new HashSet<(string, string)>();
        var obj = Path.Combine(project.ProjectFolder, "obj");
        var spec = Path.Combine(obj, Path.GetFileName(project.EntryPoint) + ".nuget.dgspec.json");
        using (var graph = await JsonAsync(spec, token).ConfigureAwait(false))
        {
            if (graph?.RootElement.ValueKind == JsonValueKind.Object && graph.RootElement.TryGetProperty("projects", out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var value in values.EnumerateObject())
                    if (Path.IsPathFullyQualified(value.Name)) projects.Add(value.Name);
        }
        foreach (var entry in projects)
        {
            var directory = Path.GetDirectoryName(entry)!;
            roots.Add(directory);
            imports.Enqueue((entry, directory));
            for (var parent = directory; parent is not null; parent = Path.GetDirectoryName(parent))
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                             "global.json", "NuGet.Config", "nuget.config", ".editorconfig", ".globalconfig" })
                {
                    var path = Path.Combine(parent, name);
                    Add(path);
                    if (Path.GetExtension(path) is ".props" or ".targets") imports.Enqueue((path, directory));
                }
        }

        var analysis = Path.Combine(obj, "Debug", "net10.0", "sunder-analysis", BuilderToolchainPlatform.Current.DotnetRid);
        using (var plan = await JsonAsync(Path.Combine(analysis, "sunder-rpc", "package-role-plan.v1.json"), token).ConfigureAwait(false))
        {
            if (plan is not null && plan.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var path in Paths(plan.RootElement, "sourceFiles", "path"))
                {
                    var full = Path.GetFullPath(path, project.ProjectFolder);
                    // Exact SDK-generated artifacts only. An authored Compile under obj is
                    // still a real input, including a file whose name ends in .g.cs.
                    if (full == Path.Combine(analysis, Path.GetFileNameWithoutExtension(project.EntryPoint) + ".GlobalUsings.g.cs")
                        || full == Path.Combine(analysis, Path.GetFileNameWithoutExtension(project.EntryPoint) + ".AssemblyInfo.cs")
                        || full == Path.Combine(analysis, ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs")) continue;
                    Add(full);
                }
                foreach (var path in Paths(plan.RootElement, "resources", "sourcePath")) Add(path);
                foreach (var name in new[] { "workerReferences", "appReferences", "workerRuntimeReferences", "appRuntimeReferences", "frameworkInputs" })
                    foreach (var path in Paths(plan.RootElement, name, "path")) Add(path);
                foreach (var role in new[] { "workerDependencies", "appDependencies" })
                    if (plan.RootElement.TryGetProperty(role, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                        foreach (var path in Paths(dependencies, "inputs", "path"))
                        {
                            if (IsRestoreOutput(path)) continue;
                            Add(path);
                            if (Path.GetExtension(path) is ".props" or ".targets"
                                && !toolingRoots.Any(root => Contains(root, path)))
                                imports.Enqueue((path, project.ProjectFolder));
                        }
            }
        }

        // A bounded notification aid, not a partial MSBuild interpreter. Literal authored
        // includes/imports expose new glob members before they appear in an evaluated plan.
        // Property/item expressions deliberately remain MSBuild's responsibility.
        while (imports.TryDequeue(out var import))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(import)) continue;
            if (visited.Count > 256) throw new InvalidDataException("Build input tracking exceeds 256 authored imports.");
            Add(import.Path);
            if (!File.Exists(import.Path)) continue;
            XDocument document;
            try { document = BuilderDotnetMetadata.ReadXml(BuilderProjectSource.Utf8.GetString(await ReadAsync(import.Path, token).ConfigureAwait(false)).TrimStart('\uFEFF')); }
            catch (Exception error) when (MetadataFailure(error) || error is XmlException) { continue; }
            foreach (var item in document.Descendants())
            {
                var isImport = item.Name.LocalName == "Import";
                if (!isImport && item.Name.LocalName is not ("Compile" or "AvaloniaXaml" or "AvaloniaResource" or "EmbeddedResource"
                    or "Content" or "None" or "AdditionalFiles" or "Analyzer" or "HintPath" or "ProjectReference")) continue;
                var value = isImport ? (string?)item.Attribute("Project") : item.Name.LocalName == "HintPath" ? item.Value : (string?)item.Attribute("Include");
                if (value is null || value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal)
                    || value.Contains("%(", StringComparison.Ordinal) || value.Contains('%')) continue;
                foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var normalized = part.Replace('\\', Path.DirectorySeparatorChar);
                    var directory = isImport ? Path.GetDirectoryName(import.Path)! : import.ProjectDirectory;
                    var wildcard = normalized.IndexOfAny(['*', '?']);
                    if (wildcard >= 0)
                    {
                        var slash = normalized.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
                        roots.Add(Path.GetFullPath(slash < 0 ? "." : normalized[..(slash + 1)], directory));
                    }
                    else
                    {
                        var path = Path.GetFullPath(normalized, directory);
                        Add(path);
                        if (isImport) imports.Enqueue((path, import.ProjectDirectory));
                        else if (item.Name.LocalName == "ProjectReference")
                        {
                            roots.Add(Path.GetDirectoryName(path)!);
                            imports.Enqueue((path, Path.GetDirectoryName(path)!));
                        }
                    }
                }
            }
        }

        if (config is not null) Add(config);
        token.ThrowIfCancellationRequested();
        return new(roots.ToArray(), files.ToArray());

        void Add(string path)
        {
            path = Path.GetFullPath(path, project.ProjectFolder);
            if (files.Count >= 32768 && !files.Contains(path)) throw new InvalidDataException("Build input tracking exceeds 32,768 files.");
            files.Add(path);
        }
        bool IsRestoreOutput(string path) => projects.Any(entry => Path.GetDirectoryName(path) == Path.Combine(Path.GetDirectoryName(entry)!, "obj")
            && (Path.GetFileName(path) == "project.assets.json" || Path.GetFileName(path) == Path.GetFileName(entry) + ".nuget.dgspec.json"
                || Path.GetFileName(path) == Path.GetFileName(entry) + ".nuget.g.props" || Path.GetFileName(path) == Path.GetFileName(entry) + ".nuget.g.targets"));
    }

    private static bool MetadataFailure(Exception error) => error is InvalidDataException or JsonException or KeyNotFoundException or FormatException
        or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException;

    private static IEnumerable<string> Paths(JsonElement value, string collection, string name)
    {
        if (!value.TryGetProperty(collection, out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var path) && path.ValueKind == JsonValueKind.String
                && path.GetString() is { Length: > 0 } text) yield return text;
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
    private static async Task<JsonDocument?> JsonAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try { return BuilderProjectSource.Json(await ReadAsync(path, token).ConfigureAwait(false)); }
        catch (Exception error) when (MetadataFailure(error)) { return null; }
    }
    private static async Task<byte[]> ReadAsync(string path, CancellationToken token)
    {
        BuilderProjectSource.EnsureSafePath(Path.GetDirectoryName(path)!, path);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Build tracking metadata exceeds 32 MiB.");
        return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
    }
}

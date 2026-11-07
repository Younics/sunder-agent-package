using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

internal static partial class BuilderLocalNuGet
{
    private const int MaximumMetadataBytes = 256 * 1024;
    private const string TemplateRoot = "content/sunder-package/";
    private const string TemplateProject = TemplateRoot + "Sunder.Package.Template/";

    private static async Task ValidatePackageAsync(string path, string id, string version, IReadOnlyDictionary<string, string> versions, CancellationToken token)
    {
        await using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > 4096) throw new InvalidDataException($"{id} must contain 1–4,096 archive entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/');
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (name.Length is 0 or > 1024 || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/').Any(part => part is "" or "." or "..") || kind is not (0 or 0x8000 or 0x4000) ||
                !entries.TryAdd(name, entry) || entry.Length > MaximumArchiveBytes || (expanded += entry.Length) > MaximumTotalBytes)
                throw new InvalidDataException($"{id} contains unsafe, duplicate, or oversized archive entries.");
        }
        ZipArchiveEntry Require(string name)
        {
            if (!entries.TryGetValue(name, out var entry) || entry.Length == 0 || entry.FullName.EndsWith('/'))
                throw new InvalidDataException($"{id} is missing required artifact {name}.");
            return entry;
        }

        var nuspecs = entries.Keys.Where(name => name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nuspecs.Length != 1 || nuspecs[0].Contains('/')) throw new InvalidDataException($"{id} must contain exactly one root .nuspec.");
        var nuspec = ParseXml(await ReadEntryAsync(Require(nuspecs[0]), token).ConfigureAwait(false));
        var metadata = nuspec.Root?.Elements().Where(element => element.Name.LocalName == "metadata").ToArray() ?? [];
        if (nuspec.Root?.Name.LocalName != "package" || metadata.Length != 1 ||
            !metadata[0].Elements().Where(element => element.Name.LocalName == "id").Select(element => element.Value).SequenceEqual([id]) ||
            !metadata[0].Elements().Where(element => element.Name.LocalName == "version").Select(element => element.Value).SequenceEqual([version]))
            throw new InvalidDataException($"{id} .nuspec ID/version must be exactly {id} {version}.");
        var dependencies = metadata[0].Descendants().Where(element => element.Name.LocalName == "dependency").ToArray();
        foreach (var dependency in dependencies)
        {
            var dependencyId = (string?)dependency.Attribute("id") ?? "";
            if (!dependencyId.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase)) continue;
            if (!versions.TryGetValue(dependencyId, out var selected) || !MatchesCoordinate((string?)dependency.Attribute("version"), selected))
                throw new InvalidDataException($"{id} has a Sunder dependency outside the selected local feed: {dependency}.");
        }
        if (id.StartsWith("Sunder.Sdk", StringComparison.Ordinal))
        {
            Require("lib/net10.0/" + id + ".dll");
            if (id != "Sunder.Sdk" && !dependencies.Any(dependency => (string?)dependency.Attribute("id") == "Sunder.Sdk"))
                throw new InvalidDataException($"{id} must declare its Sunder.Sdk dependency.");
            if (id == "Sunder.Sdk.Worker" && dependencies.Any(dependency => (string?)dependency.Attribute("id") == "Sunder.Sdk" &&
                (string?)dependency.Attribute("version") != "[" + versions["Sunder.Sdk"] + "]"))
                throw new InvalidDataException("Sunder.Sdk.Worker must pin its exact selected Sunder.Sdk dependency.");
            if (id == "Sunder.Sdk.Stacks")
            {
                Require("contracts/sunder.stack.contributor.rpc.json");
                Require("buildTransitive/Sunder.Sdk.Stacks.props");
            }
        }
        else if (id == "Sunder.Package.Build")
        {
            foreach (var name in new[] { "props", "targets", "SourceLock.targets", "Authoring.targets", "Authoring.Outer.targets", "Authoring.Dispatch.targets",
                "Authoring.References.targets", "Authoring.RoleInputs.targets", "Authoring.CrossTargeting.targets" })
                Require("buildTransitive/Sunder.Package.Build." + name);
            foreach (var name in new[] { "Sunder.Package.Build", "Sunder.Package.Format", "Sunder.Sdk", "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
                "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions" }) Require("tasks/net10.0/" + name + ".dll");
            Require("analyzers/dotnet/cs/Sunder.Package.Build.Editor.dll");
        }
        else
        {
            // Direct archive installation is offline: template packages must not have NuGet
            // dependencies. All project dependencies are restored with the scoped config later.
            if (dependencies.Length != 0 || !metadata[0].Descendants().Any(element => element.Name.LocalName == "packageType" && (string?)element.Attribute("name") == "Template"))
                throw new InvalidDataException("Sunder.Package.Templates must be a dependency-free Template package.");
            using var template = BuilderProjectSource.Json(await ReadEntryAsync(Require(TemplateRoot + ".template.config/template.json"), token).ConfigureAwait(false));
            var value = template.RootElement;
            var symbols = value.GetProperty("symbols");
            if (value.GetProperty("identity").GetString() != "Sunder.Package" || value.GetProperty("shortName").GetString() != "sunder-package")
                throw new InvalidDataException("Sunder template identity must match the canonical package template.");
            var legacyRange = Constant(symbols, "coordinatedPackageVersionRange");
            var project = ParseXml(await ReadEntryAsync(Require(TemplateProject + "Sunder.Package.Template.csproj"), token).ConfigureAwait(false));
            var frameworks = project.Descendants("TargetFramework").Select(element => element.Value).ToArray();
            if (frameworks.Length != 1 || !System.Text.RegularExpressions.Regex.IsMatch(frameworks[0], @"^net[1-9][0-9]\.0$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new InvalidDataException("Sunder template must declare one canonical .NET Worker target framework.");
            RequireReference(project, "Sunder.Sdk.Worker", versions, legacyRange);
            RequireReference(project, "Sunder.Package.Build", versions, legacyRange);
            foreach (var (folder, package) in new[] { ("Views/Avalonia", "Sunder.Sdk.Avalonia"), ("Stacks/Stacks", "Sunder.Sdk.Stacks") })
                RequireReference(ParseXml(await ReadEntryAsync(Require(TemplateProject + folder + ".PackageReferences.props"), token).ConfigureAwait(false)), package, versions, legacyRange);
        }
    }

    // Exact or bounded NuGet constraints only; actual selected dependency versions
    // are independent of the archive's own version. No public Sunder fallback.
    private static bool MatchesCoordinate(string? constraint, string version)
    {
        if (constraint == version || constraint == "[" + version + "]") return true;
        if (constraint is null || constraint.Length > 1024 || constraint.Length < 5 ||
            constraint[0] is not ('[' or '(') || constraint[^1] is not (']' or ')') || !SemanticVersion.TryParse(version, out var selected)) return false;
        var bounds = constraint[1..^1].Split(',', StringSplitOptions.TrimEntries);
        if (bounds.Length != 2 || !SemanticVersion.TryParse(bounds[0], out var lower) || !SemanticVersion.TryParse(bounds[1], out var upper)) return false;
        return (constraint[0] == '[' ? selected.ComparePrecedenceTo(lower) >= 0 : selected.ComparePrecedenceTo(lower) > 0)
            && (constraint[^1] == ']' ? selected.ComparePrecedenceTo(upper) <= 0 : selected.ComparePrecedenceTo(upper) < 0);
    }

    private static string? Constant(JsonElement symbols, string name)
    {
        if (!symbols.TryGetProperty(name, out var symbol)) return null;
        if (symbol.GetProperty("type").GetString() != "generated" || symbol.GetProperty("generator").GetString() != "constant") return null;
        return symbol.GetProperty("parameters").GetProperty("value").GetString();
    }

    private static void RequireReference(XDocument project, string id, IReadOnlyDictionary<string, string> versions, string? legacyRange)
    {
        var references = project.Descendants("PackageReference").Where(element => (string?)element.Attribute("Include") == id).ToArray();
        var range = references.Length == 1 ? (string?)references[0].Attribute("Version") : null;
        if (range == "SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE") range = legacyRange;
        if (!MatchesCoordinate(range, versions[id])) throw new InvalidDataException($"Sunder template dependency {id} does not admit its selected local version.");
    }

    private static XDocument ParseXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumMetadataBytes });
        return XDocument.Load(reader);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > MaximumMetadataBytes) throw new InvalidDataException($"Local NuGet metadata {entry.FullName} exceeds 256 KiB.");
        await using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException("Local NuGet metadata exceeds its declared entry size.");
        return bytes;
    }
}

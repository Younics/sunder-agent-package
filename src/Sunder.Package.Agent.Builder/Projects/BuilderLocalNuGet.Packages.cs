using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Sunder.Package.Agent.Builder;

internal static partial class BuilderLocalNuGet
{
    private const int MaximumMetadataBytes = 256 * 1024;
    private const string TemplateRoot = "content/sunder-package/";
    private const string TemplateProject = TemplateRoot + "Sunder.Package.Template/";

    private static async Task ValidatePackageAsync(string path, string id, string version, CancellationToken token)
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
            if (!PackageIds.Contains(dependencyId, StringComparer.Ordinal) || !MatchesCoordinate((string?)dependency.Attribute("version"), version))
                throw new InvalidDataException($"{id} has a Sunder SDK dependency outside the coordinated local feed: {dependency}.");
        }
        if (id.StartsWith("Sunder.Sdk", StringComparison.Ordinal))
        {
            Require("lib/net10.0/" + id + ".dll");
            if (id != "Sunder.Sdk" && !dependencies.Any(dependency => (string?)dependency.Attribute("id") == "Sunder.Sdk"))
                throw new InvalidDataException($"{id} must declare its coordinated Sunder.Sdk dependency.");
            if (id == "Sunder.Sdk.Worker" && dependencies.Any(dependency => (string?)dependency.Attribute("id") == "Sunder.Sdk" &&
                (string?)dependency.Attribute("version") != "[" + version + "]"))
                throw new InvalidDataException("Sunder.Sdk.Worker must pin its Sunder.Sdk dependency to the exact same release.");
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
            if (value.GetProperty("identity").GetString() != "Sunder.Package" || value.GetProperty("shortName").GetString() != "sunder-package" ||
                Constant(symbols, "coordinatedPackageVersion") != version || !MatchesCoordinate(Constant(symbols, "coordinatedPackageVersionRange"), version))
                throw new InvalidDataException("Sunder template identity and coordinated SDK version/range must match the local feed.");
            var project = ParseXml(await ReadEntryAsync(Require(TemplateProject + "Sunder.Package.Template.csproj"), token).ConfigureAwait(false));
            if (!project.Descendants("TargetFramework").Select(element => element.Value).SequenceEqual(["net10.0"]))
                throw new InvalidDataException("Sunder template must target the supported net10.0 SDK.");
            RequireReference(project, "Sunder.Sdk.Worker");
            RequireReference(project, "Sunder.Package.Build");
            foreach (var (folder, package) in new[] { ("Views/Avalonia", "Sunder.Sdk.Avalonia"), ("Stacks/Stacks", "Sunder.Sdk.Stacks") })
                RequireReference(ParseXml(await ReadEntryAsync(Require(TemplateProject + folder + ".PackageReferences.props"), token).ConfigureAwait(false)), package);
        }
    }

    // This optional source-pack override accepts the coordinated forms emitted by Core,
    // not floating versions or arbitrary ranges that can select another SDK release.
    private static bool MatchesCoordinate(string? constraint, string version)
    {
        if (constraint == version || constraint == "[" + version + "]") return true;
        if (version.Contains('-')) return false;
        if (!Version.TryParse(version, out var parsed) || parsed.Minor == int.MaxValue) return false;
        // Core's template constant uses the compact form; NuGet's pack writer emits
        // the same bounded range with a space after the comma in the .nuspec.
        var lower = $"{parsed.Major}.{parsed.Minor}.0";
        var upper = $"{parsed.Major}.{parsed.Minor + 1}.0";
        return constraint == $"[{lower},{upper})" || constraint == $"[{lower}, {upper})";
    }

    private static string? Constant(JsonElement symbols, string name)
    {
        var symbol = symbols.GetProperty(name);
        if (symbol.GetProperty("type").GetString() != "generated" || symbol.GetProperty("generator").GetString() != "constant") return null;
        return symbol.GetProperty("parameters").GetProperty("value").GetString();
    }

    private static void RequireReference(XDocument project, string id)
    {
        var references = project.Descendants("PackageReference").Where(element => (string?)element.Attribute("Include") == id).ToArray();
        if (references.Length != 1 || (string?)references[0].Attribute("Version") != "SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE")
            throw new InvalidDataException($"Sunder template must use its coordinated SDK range for {id}.");
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal const string PreCanonicalProjectsDirectory = "tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects";

    // Recorded bytes and reviewed replacement bytes are separate evidence. Changing
    // any dependency, Compile/None/resource item, condition, import or output property
    // requires an explicit review here; a historical fixture never exempts the root.
    internal static readonly CanonicalProjectHistory[] CanonicalProjectHistories =
    [
        new("Agent", "Agent.index.csproj.txt", "75644bb377059b386c7096d1dccd41e83e1465f0cc00ecd2cc6aa7229b3cbe69", "77a9a8b360ca6d45c56a8587e743d7bde39a92f9b6e8af83743e56aaa43dd0e5"),
        new("Builder", "Builder.csproj.txt", "e2db5d64b5e727049728bef76ad0ba65675b45a2b73fa3a8c11ac6e9d18d367a", "fc3d2e8c7bd20459136d72c2ce353d6339296dbfce3884d97802d59684c73872"),
        new("Execution.Docker", "Execution.Docker.csproj.txt", "7b24bf8d7bfa88b49c36c73dbcf74fb218b5676e2364eae2b9c6a1c209b23ae5", "918041cd3837470499c522fff4565aa9ceb018cb0173a8ba83280079e72e2fe4"),
        new("Execution.Local", "Execution.Local.csproj.txt", "795e324ac6a079d75cbbb1cd0caa95af64a86c21c5ef4a563a71009c0538821a", "3a2c9757557b6463a8a9b0feaede06b0a23448e5f2c259e551fbdcc0a9e4101b"),
        new("Mcp", "Mcp.csproj.txt", "d755f0af3f768409a8805fe442f217572af5f5c35dfcc4c903b7ada4d6e02d26", "543f628ac416a2737eadc2006e004399ca9450793be6d250db579a26eae24e3e"),
        new("Memory.Semantic", "Memory.Semantic.csproj.txt", "2c3596d573ef989189db72669d1072bbc0f899590042d9f2b2c9c129907880b5", "c410c1b8ef55de68d8f7be42915fef1d9666b6f9bba8d7449f9e0a0b6fcbdc8e"),
        new("Provider.Anthropic", "Provider.Anthropic.csproj.txt", "64550bcad29f0dae221d27d4e03463d2946bfee33aef91a4598109db0b10ae92", "5a12355ce5ed3adbce1f1571e82379686ff15c9be6198e4dffb230d3604f96c7"),
        new("Provider.Gemini", "Provider.Gemini.csproj.txt", "0dc4793043b846d2a0536c6b13b1547039dea59485d36fa7bc3111498fc2a58a", "eca974544c922a4cecdd8a04e8177b43af23664cf80a330f3b63a0e8c4724125"),
        new("Provider.LMStudio", "Provider.LMStudio.csproj.txt", "31495d0536fc0cf9bf94a522a713db8ff881de04154e8e2dcd577dbf894659f8", "a0b9608997874321d25bf000bc2db0bc52c3280eab1539e307d8a8b38229af79"),
        new("Provider.OpenAI", "Provider.OpenAI.csproj.txt", "fc7f57aa46854d74efa522412fe9a0c51169cadf5ffc8f36f5b208d3a985a5ac", "2dbf4a91af341e58f897fbf92663fac6f5bbf95e61461bba98949ce145ad71fe"),
        new("Skills", "Skills.csproj.txt", "b1f3de0be58aa9e3931eae4b742c876fed5a283e61e7bdc04224f505e3514ae2", "4efbe69c244540be8e71f759fc7b0a08aef73b4569a9a7784574d14bc7ba1ef2"),
        new("Subagents", "Subagents.csproj.txt", "47a3bb11ce767546465cc4a3dc91065e9e03fc053d51dd6c41278aedbfc97e67", "4154ed0c53a7ce92685dfa4052228f60f950a9e336727382ebce9a2d5e2c3095"),
        new("Tools.Files", "Tools.Files.csproj.txt", "a645b82958f192fc9219e73b6adee7c26a97c251ce1ee3144ea5486c3fc9d5f9", "b64571698c922ab5dfb589ba41438f32365586b96b9222ed42c34fa3fe1d4c3f"),
        new("Tools.Shell", "Tools.Shell.csproj.txt", "6550360dec84c2123a45d2b9bff00ba3f7b6df5f63e5a9751fdaaf7ac5a4d11a", "34b8add95f800cf03f3b8e8eee4417bb1e8ea2c6ee1c417b38f881bbf4a824af"),
        new("Tools.Web", "Tools.Web.csproj.txt", "a8a51776f9b473fa284abc4e450edd96bdffbf93c3afe319479b637f7ca451c8", "46d40ef3435fcb86d8cdc7ea62d1acd9641da7c1af923b48f701bb0657eb8753"),
    ];

    // Include imported and automatic repository build inputs: guarding just the
    // csproj would miss changed transitive Compile/resource items and dependency versions.
    private static readonly IReadOnlyDictionary<string, string> CanonicalSupportHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Directory.Build.props"] = "98df4970c32b50db24d5a52b0fbfb4c6d021aa57fab27c88ba2a9f13f4ea7299",
        ["Directory.Build.targets"] = "f2c10a5ee8cd8abce06b90def0894fc8ae22ba5f9d27658544ddde97a15434e9",
        ["Directory.Packages.props"] = "900031df619b67cbf13dfedc2e820ea87868757546a1bd374f7be9d56b193747",
        ["Sunder.Agent.Worker.props"] = "e477bd25c15c7106331b225c4a47db92a2057f5493d4039880827a38603fd40e",
        ["src/Sunder.Package.Agent/Models/AgentExtensionSources.props"] = "d27399b4e4d279baad2b1d9760f6c4142a962b971c79195365d49bca54df0a1a",
        ["src/Sunder.Package.Agent.Shared/Sunder.Package.Agent.Shared.props"] = "834d0e4c4eda9112795240b3e389f18ddb32bce01dc1d4959e79c5fa2055de63",
        ["src/Sunder.Package.Agent.Shared/Importing/Sunder.Package.Agent.Importing.props"] = "9cf7fb591e23dccabf0688dddd3aca1dd432bde9f26d0441c302f7bd2d884aef",
        ["src/Sunder.Package.Agent.Provider.Shared/ProviderSettingsSources.props"] = "4aee4334865bf3ffddbd0fc0e8b49340900affecaee2fe43f73e601a3389a1c3",
    };

    internal static string ReadMsBuildTextBeforeCanonicalReplacement(string root, string path, string text)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (TryReadRetirementProject(root, relative, text, out var historicalProject)) return historicalProject;
        var history = CanonicalProjectHistories.SingleOrDefault(project => project.Project == relative);
        if (history is not null)
        {
            AssertCanonicalProject(root, history, text);
            var recorded = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, history.Fixture));
            AssertProjectHash(history.RecordedSha256, recorded, history.Fixture);
            return recorded;
        }

        if (relative == "Directory.Build.targets")
        {
            AssertCanonicalSupport(root, relative);
            AssertCanonicalTargets(XDocument.Parse(text));
            text = ReadPhysicalCleanupHistory(root).Single(change => change.Path == relative).BeforeText;
            const string current = "<ItemGroup Condition=\"'$(SunderPackageProject)' == 'true' and '$(SunderPackageRole)' != ''\">";
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            return text.Replace(current, "<ItemGroup Condition=\"'$(SunderPackageProject)' == 'true'\">", StringComparison.Ordinal);
        }

        if (relative is "src/Sunder.Package.Agent/Models/AgentExtensionSources.props"
            or "src/Sunder.Package.Agent.Provider.Shared/ProviderSettingsSources.props")
        {
            // These are new canonical inputs, not pre-migration declarations.
            // Check their entire current content before excluding them from history.
            AssertCanonicalSupport(root, relative);
            return "<Project />";
        }
        return text;
    }

    internal static void AssertCanonicalProjectTopology(string root)
    {
        Assert.Equal(15, CanonicalProjectHistories.Length);
        foreach (var path in CanonicalSupportHashes.Keys) AssertCanonicalSupport(root, path);
        var fixtures = Directory.EnumerateFiles(Path.Combine(root, PreCanonicalProjectsDirectory), "*.csproj.txt")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(CanonicalProjectHistories.Select(project => project.Fixture).Order(StringComparer.Ordinal), fixtures);

        using var packages = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "packages.json")));
        var entries = packages.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(CanonicalProjectHistories.Select(project => project.Project).Order(StringComparer.Ordinal),
            entries.Select(entry => entry.GetProperty("projectPath").GetString()).Order(StringComparer.Ordinal));
        var solution = XDocument.Load(Path.Combine(root, "Sunder.AgentPackage.slnx"));
        AssertCanonicalTargets(XDocument.Parse(ReadNormalizedProjectText(Path.Combine(root, "Directory.Build.targets"))));
        foreach (var history in CanonicalProjectHistories)
        {
            var entry = Assert.Single(entries, entry => entry.GetProperty("projectPath").GetString() == history.Project);
            Assert.Equal(history.PackageId, entry.GetProperty("packageId").GetString());
            Assert.Equal("sunderpkg", entry.GetProperty("artifactType").GetString());
            Assert.Single(solution.Descendants("Project"), project => ((string?)project.Attribute("Path"))?.Replace('\\', '/') == history.Project);
            AssertCanonicalProject(root, history, ReadNormalizedProjectText(Path.Combine(root, history.Project)));
        }
    }

    private static void AssertCanonicalProject(string root, CanonicalProjectHistory history, string text)
    {
        if (TryReadNaturalServiceDelta(root, Path.Combine(root, history.Project), text, out var preNatural)) text = preNatural;
        AssertProjectHash(history.CanonicalSha256, text, history.Project);
        var document = XDocument.Parse(text);
        Assert.Equal("Microsoft.NET.Sdk", (string?)document.Root!.Attribute("Sdk"));
        Assert.Equal(history.PackageId, Assert.Single(document.Descendants("SunderPackageFamilyId")).Value);
        foreach (var item in new[] { "ProjectReference", "SunderPackageRole", "SunderPackageAppProject", "SunderPackagePrimaryProject",
                     "SunderRpcBinding", "SunderRpcProvider", "SunderUsesContract", "SunderContractBundle", "SunderPackageTarget", "Target" })
            Assert.Empty(document.Descendants(item));
        Assert.DoesNotContain(document.Descendants("Compile"), item => item.Attribute("Remove") is not null);
        Assert.DoesNotContain(document.Descendants("None"), item => item.Attribute("Remove") is not null);
        Assert.Equal(history.HasApp ? new[] { "true" } : [], document.Descendants("SunderSdkAvaloniaReference").Select(value => value.Value));

        var defaults = history.Family == "Agent" ? document : XDocument.Parse(AssertCanonicalSupport(root, "Sunder.Agent.Worker.props"));
        Assert.Equal("true", Assert.Single(defaults.Descendants("SunderPackageProject")).Value);
        Assert.Equal("true", Assert.Single(defaults.Descendants("SunderSdkWorkerReference")).Value);
        foreach (var property in new[] { "SelfContained", "UseAppHost", "PublishSingleFile", "PublishTrimmed", "EnableRuntimePackDownload", "EnableAppHostPackDownload" })
            Assert.Equal("false", Assert.Single(defaults.Descendants(property)).Value);
        Assert.Equal("win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64", Assert.Single(defaults.Descendants("RuntimeIdentifiers")).Value);
        Assert.All(defaults.Descendants("OutputType"), property => Assert.Equal("Library", property.Value));

        foreach (var import in document.Descendants("Import"))
        {
            var include = import.Attribute("Project")!.Value.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.Ordinal).Replace('\\', '/');
            var importedPath = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(history.Project)!, include));
            AssertCanonicalSupport(root, Path.GetRelativePath(root, importedPath).Replace('\\', '/'));
        }
        foreach (var directory in new[] { "src", Path.GetDirectoryName(history.Project)! })
            foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                Assert.False(File.Exists(Path.Combine(root, directory, file)), $"Unreviewed canonical build override: {directory}/{file}");
        foreach (var path in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" }) AssertCanonicalSupport(root, path);
    }

    private static void AssertCanonicalTargets(XDocument document)
    {
        foreach (var name in new[] { "SunderContractBundle", "SunderUsesContract", "SunderRpcProvider", "SunderRpcBinding", "SunderPackageAppProject", "SunderPackagePrimaryProject" })
            Assert.Empty(document.Descendants(name));
        var stacks = Assert.Single(document.Descendants("PackageReference"), reference => (string?)reference.Attribute("Include") == "Sunder.Sdk.Stacks");
        Assert.Equal("buildTransitive", (string?)stacks.Attribute("ExcludeAssets"));
        Assert.Equal("'$(SunderSdkStacksReference)' == 'true' and '$(SunderUseSourceSdk)' == 'false'", (string?)stacks.Parent!.Attribute("Condition"));
        Assert.DoesNotContain(document.Descendants("ProjectReference"), reference => ((string?)reference.Attribute("Include"))?.Contains("SunderPackageAppProject", StringComparison.Ordinal) == true);
    }

    private static string AssertCanonicalSupport(string root, string relative)
    {
        Assert.True(CanonicalSupportHashes.TryGetValue(relative, out var expected), $"Unreviewed canonical import: {relative}");
        var text = ReadNormalizedProjectText(Path.Combine(root, relative));
        if (TryReadNaturalServiceDelta(root, Path.Combine(root, relative), text, out var preNatural)) text = preNatural;
        AssertProjectHash(expected!, text, relative);
        return text;
    }

    internal static string ReadNormalizedProjectText(string path)
        => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static string ProjectTextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void AssertProjectHash(string expected, string text, string path)
        => Assert.True(expected == ProjectTextHash(text), $"Project migration evidence drift: {path}. Expected {expected}; actual {ProjectTextHash(text)}.");

    internal sealed record CanonicalProjectHistory(string Family, string Fixture, string RecordedSha256, string CanonicalSha256)
    {
        internal string Name => Family == "Agent" ? "Sunder.Package.Agent" : "Sunder.Package.Agent." + Family;
        internal string Project => $"src/{Name}/{Name}.csproj";
        internal string PackageId => Name.ToLowerInvariant();
        internal bool HasApp => !Family.StartsWith("Tools.", StringComparison.Ordinal);
    }
}

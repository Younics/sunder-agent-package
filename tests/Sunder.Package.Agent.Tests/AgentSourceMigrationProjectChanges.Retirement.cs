using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static IReadOnlyDictionary<string, RetirementCapture> RetirementCaptures => RetirementHistory.Captures;

    // Formatting and test-local C# alias names are not the graph. Every property,
    // condition, role-selection target, dependency, Compile/resource item and import
    // remains in this signature. Alias naming is checked by the owning compilation.
    private static readonly IReadOnlyDictionary<string, string> RetainedProjectSurfaces = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["src/Sunder.Agent.Execution.Common/Sunder.Agent.Execution.Common.csproj"] = "9e22a97df60b4a8b6d99b0d9e782c1b7c3a67b5c1d31b028e89db64c14585803",
        // Local execution links the generated-role tool probe owned by the test suite.
        ["tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj"] = "08c0db9cc902b9a7b0767e162f28660b644c66057062aba976cace24adf78350",
        ["tests/Sunder.Package.Agent.Memory.Semantic.Tests/Sunder.Package.Agent.Memory.Semantic.Tests.csproj"] = "eadca25f75fe1198621a424fa849f09d7be94dd19196150e2a63409ae3365276",
        ["tests/Sunder.Package.Agent.Presentation.State.Tests/Sunder.Package.Agent.Presentation.State.Tests.csproj"] = "aa5d322a2f12db088f356a5471a9995eb7af6e2e548baa956f62f914cf0e0ad7",
        // Presentation links the owning AppViews/AppInfrastructure and Memory consumer split.
        ["tests/Sunder.Package.Agent.Presentation.Tests/Sunder.Package.Agent.Presentation.Tests.csproj"] = "5bcacf8549afb3d3cdb34dea1c1160b91c7ea94d8e15504b3c436fe9d59147bc",
        ["tests/Sunder.Package.Agent.Provider.Anthropic.Tests/Sunder.Package.Agent.Provider.Anthropic.Tests.csproj"] = "c9c2d1c6e1c4eb0d281734109e9802ba809631f354f5576d93196b598a1f0e51",
        ["tests/Sunder.Package.Agent.Provider.Gemini.Tests/Sunder.Package.Agent.Provider.Gemini.Tests.csproj"] = "6a97e17c52ce8818da16237bf133c2b0702de18735cea38d3822f3574837631e",
        ["tests/Sunder.Package.Agent.Provider.LMStudio.Tests/Sunder.Package.Agent.Provider.LMStudio.Tests.csproj"] = "65ef75f389b48011171c5bd6e10ac142631dc7f2e6409e5313e439ce287eba5d",
        ["tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj"] = "8e638979759ca268d91ec5f308670ba157eb7662359ad6552f47cf66167f371c",
        ["tests/Sunder.Package.Agent.Provider.TestSupport/Sunder.Package.Agent.Provider.TestSupport.csproj"] = "1a0aeddfbb6c7711622f8ad50f4c9a9228d85013ed624ea652c0815346779b19",
        ["tests/Sunder.Package.Agent.Skills.Tests/Sunder.Package.Agent.Skills.Tests.csproj"] = "e3ccf028e8239c58b857085baa5bbf45569357f2c948c28162df1416f4cd8e45",
        ["tests/Sunder.Package.Agent.Subagents.Tests/Sunder.Package.Agent.Subagents.Tests.csproj"] = "7ec2e4ce6077ccd7f83a1bd50ca6b537fae68ee7745e9db7ce5d4b5009530054",
        ["tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj"] = "267093164973408367ab0f61df0a43539df57c93994805bbbc434eae77b3cbae",
        ["tests/RegressionTestHarness.props"] = "0845502b98b82a860906378ff82480d3219c72e32a7f39aa51f2caf4d5c558de",
    };

    internal static string ProjectSurfaceSignature(string text)
    {
        var result = new StringBuilder();
        Append(XDocument.Parse(text).Root!);
        return result.ToString();

        void Append(XElement element)
        {
            result.Append(element.Name).Append("\n{\n");
            foreach (var attribute in element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal))
                if (element.Name != "ProjectReference" || attribute.Name != "Aliases")
                    result.Append(attribute.Name).Append('=').Append(attribute.Value.Trim().Replace('\\', '/')).Append('\n');
            if (element.HasElements) foreach (var child in element.Elements()) Append(child);
            else result.Append("value=").Append(element.Value.Trim().Replace('\\', '/')).Append('\n');
            result.Append("}\n");
        }
    }

    private static void AssertRetainedProjectSurface(string root, string relative, string current)
    {
        Assert.True(File.Exists(Path.Combine(root, relative)), $"Retained project/input is missing: {relative}");
        AssertProjectHash(RetainedProjectSurfaces[relative], ProjectSurfaceSignature(current), relative + " (MSBuild surface)");
        var document = XDocument.Parse(current);
        foreach (var reference in document.Descendants("ProjectReference"))
        {
            var include = reference.Attribute("Include")!.Value.Replace('\\', '/');
            if (include.Contains("$(", StringComparison.Ordinal)) continue; // Exact property reference and condition are signed above.
            var target = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(relative)!, include))).Replace('\\', '/');
            Assert.False(RetirementCaptures.TryGetValue(target, out var obsolete) && obsolete.Kind is "retired-project" or "retired-fixture-project", $"Live project reference to {target}");
            var canonical = CanonicalProjectHistories.SingleOrDefault(project => project.Project == target);
            if (canonical is null) continue;
            Assert.Equal("GetSunderPackageRoleReference", (string?)reference.Attribute("Targets"));
            var selection = (string?)reference.Attribute("AdditionalProperties");
            Assert.Contains(selection, canonical.HasApp
                ? new[] { "SunderPackageReferenceRole=worker", "SunderPackageReferenceRole=app" }
                : ["SunderPackageReferenceRole=worker"]);
            AssertCanonicalProject(root, canonical, ReadNormalizedProjectText(Path.Combine(root, target)));
        }
        foreach (var import in document.Descendants("Import"))
        {
            var include = import.Attribute("Project")!.Value.Replace('\\', '/');
            var target = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(relative)!, include))).Replace('\\', '/');
            Assert.Equal("tests/RegressionTestHarness.props", target);
            AssertRetainedProjectSurface(root, target, ReadNormalizedProjectText(Path.Combine(root, target)));
        }
    }

    private static bool TryReadRetirementProject(string root, string relative, string current, out string history)
    {
        history = string.Empty;
        if (relative == "tests/RegressionTestHarness.props")
        {
            AssertRetainedProjectSurface(root, relative, current);
            history = "<Project />"; // A checked new test input has no original declarations.
            return true;
        }
        if (!RetirementCaptures.TryGetValue(relative, out var capture)) return false;
        var project = capture.Kind is "retained-project" or "retired-project" or "retired-fixture-project";
        var support = capture.Kind == "retired-support" && Path.GetExtension(relative) is ".props" or ".targets";
        if (!project && !support) return false;
        if (capture.Kind == "retained-project") AssertRetainedProjectSurface(root, relative, current);
        else AssertRetirementReplacement(root, capture);
        history = capture.Text;
        return true;
    }

    internal static string ReadMsBuildHistory(string root, string path)
        => ReadMsBuildTextBeforeCanonicalReplacement(root, path, File.Exists(path) ? ReadNormalizedProjectText(path) : string.Empty);

    internal static XDocument ReadMsBuildHistoryDocument(string root, string path)
        // XDocument.Load accepts a UTF-8 BOM, whereas Parse receives decoded text.
        // Keep the BOM in the byte/hash evidence and discard it only for XML parsing.
        => XDocument.Parse(ReadMsBuildHistory(root, path).TrimStart('\uFEFF'));

    // Old code is data only. This reader preserves path-specific inverse operations
    // after physical removal; it never gives the compiler a legacy source file.
    internal static string ReadMigrationSourceText(string path)
        => ReadMigrationSourceText(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path);

    internal static string ReadMigrationSourceText(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!RetirementCaptures.TryGetValue(relative, out var capture) || capture.Kind == "retained-project")
            return ReadNormalizedProjectText(path);
        AssertRetirementReplacement(root, capture);
        return capture.Text;
    }

    internal static IEnumerable<string> EnumerateMigrationHistoryFiles(string root, string directory, string pattern)
    {
        var prefix = Path.GetRelativePath(root, directory).Replace('\\', '/').TrimEnd('/') + "/";
        var current = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Where(AgentPackageRepositoryInventory.IsSourceFile)
            : [];
        var historical = RetirementCaptures.Values.Where(capture => capture.Kind.StartsWith("retired-", StringComparison.Ordinal)
                && capture.Path.StartsWith(prefix, StringComparison.Ordinal)
                && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(capture.Path)))
            .Select(capture => Path.Combine(root, capture.Path));
        return current.Concat(historical).Distinct(StringComparer.Ordinal);
    }

    internal static bool HistoricalSolutionMembership(string relative, bool current)
        => RetirementCaptures.TryGetValue(relative, out var capture) && capture.Kind == "retired-project" || current;

    internal static IEnumerable<string> RetirementValidationInputs(string root)
        => CanonicalSupportHashes.Keys.Concat(RetainedProjectSurfaces.Keys)
            .Concat(CanonicalProjectHistories.Select(project => project.Project))
            .Concat(CanonicalProjectHistories.Select(project => PreCanonicalProjectsDirectory + "/" + project.Fixture))
            .Concat(RetirementDispositions.Values.SelectMany(disposition => disposition.Replacements))
            .Concat(PackedProtocolHistory.Bundle.Replacements)
            .Concat(new[] { "packages.json", "Sunder.AgentPackage.slnx" })
            .Concat(new[] { "src", "tests", "scripts" }.SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*.csproj", SearchOption.AllDirectories))
                .Where(AgentPackageRepositoryInventory.IsSourceFile).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(path => !RetirementCaptures.TryGetValue(path, out var capture) || !capture.Kind.StartsWith("retired-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    internal static void AssertRetirementCurrentTopology(string root)
    {
        AssertCanonicalProjectTopology(root);
        foreach (var capture in RetirementCaptures.Values)
        {
            if (capture.Kind == "retained-project") AssertRetainedProjectSurface(root, capture.Path, ReadNormalizedProjectText(Path.Combine(root, capture.Path)));
            else AssertRetirementReplacement(root, capture);
        }
        var retired = RetirementCaptures.Values.Where(capture => capture.Kind.StartsWith("retired-", StringComparison.Ordinal))
            .Select(capture => capture.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var directory in new[] { "src", "tests", "scripts", "fixtures" }.Where(directory => Directory.Exists(Path.Combine(root, directory))))
            foreach (var project in Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories)
                         .Where(AgentPackageRepositoryInventory.IsSourceFile).Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets"))
            {
                var relative = Path.GetRelativePath(root, project).Replace('\\', '/');
                if (retired.Contains(relative)) continue;
                var document = XDocument.Load(project);
                foreach (var item in document.Descendants().Where(item => item.Name is { LocalName: "ProjectReference" or "Compile" or "Import" }))
                {
                    var values = ((string?)item.Attribute(item.Name == "Import" ? "Project" : "Include"))?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
                    foreach (var value in values)
                    {
                        if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains('*')) continue;
                        var target = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, value.Replace('\\', '/')))).Replace('\\', '/');
                        Assert.DoesNotContain(target, retired);
                    }
                }
            }
        foreach (var entry in XDocument.Load(Path.Combine(root, "Sunder.AgentPackage.slnx")).Descendants("Project"))
            Assert.True(File.Exists(Path.Combine(root, entry.Attribute("Path")!.Value.Replace('\\', '/'))), $"Stale solution entry: {entry}");
    }

    private static void AssertRetirementReplacement(string root, RetirementCapture capture)
    {
        var path = Path.Combine(root, capture.Path);
        Assert.False(File.Exists(path), $"Physically retired input was reintroduced: {capture.Path}");
        if (capture.Kind.StartsWith("retired-fixture-", StringComparison.Ordinal))
        {
            AssertPackedProtocolReplacement(root);
            return;
        }
        var owner = capture.Path.Split('/')[1];
        if (owner == "Sunder.Package.Agent.Contracts") owner = "Sunder.Package.Agent";
        var canonical = Assert.Single(CanonicalProjectHistories, project => project.Name == owner);
        AssertCanonicalProject(root, canonical, ReadNormalizedProjectText(Path.Combine(root, canonical.Project)));
        if (capture.Kind == "retired-project") return;
        var disposition = RetirementDispositions[capture.Path];
        Assert.NotEmpty(disposition.Replacements);
        Assert.Equal("canonical-generated-behavior", disposition.Disposition);
        foreach (var replacement in disposition.Replacements)
        {
            Assert.False(RetirementCaptures.ContainsKey(replacement), $"Retirement replacement is itself historical: {replacement}");
            Assert.True(File.Exists(Path.Combine(root, replacement)), $"Missing canonical replacement for {capture.Path}: {replacement}");
            if (replacement.EndsWith(".cs", StringComparison.Ordinal) && replacement.StartsWith("src/" + owner + "/", StringComparison.Ordinal))
            {
                var source = replacement[("src/" + owner + "/").Length..];
                foreach (var removal in XDocument.Load(Path.Combine(root, canonical.Project)).Descendants("Compile")
                             .SelectMany(item => ((string?)item.Attribute("Remove"))?.Replace('\\', '/').Split(';') ?? []))
                    Assert.False(MatchesSourcePattern(removal, source), $"Replacement source is excluded from the canonical input: {replacement}");
            }
        }
        Assert.Contains("AgentSourceArtifactMigrationTests.CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload", disposition.RequiredTests);
    }

    private static bool MatchesSourcePattern(string pattern, string path)
        => Regex.IsMatch(path, "^" + Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);

    private static IReadOnlyDictionary<string, RetirementDisposition> RetirementDispositions => RetirementHistory.Dispositions;

    private static class RetirementHistory
    {
        internal static readonly IReadOnlyDictionary<string, RetirementCapture> Captures = ReadCaptures();
        internal static readonly IReadOnlyDictionary<string, RetirementDisposition> Dispositions = ReadDispositions();

        private static IReadOnlyDictionary<string, RetirementCapture> ReadCaptures()
        {
            var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
            var text = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, "retirement-history.json"));
            AssertProjectHash("271c798f547bd3e0a4fb6b26bac5bfc7acc184768c0936cef12188dacb4499d3", text, "retirement-history.json");
            var bundle = JsonSerializer.Deserialize<RetirementBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(1, bundle.SchemaVersion);
            AssertProjectHash(bundle.BaselineSha256, ReadNormalizedProjectText(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")), "immutable RPC baseline");
            Assert.Equal(13, bundle.Entries.Count(entry => entry.Kind == "retained-project"));
            Assert.Equal(25, bundle.Entries.Count(entry => entry.Kind == "retired-project"));
            Assert.Equal(154, bundle.Entries.Count(entry => entry.Kind == "retired-source"));
            using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")));
            var originalProjects = baseline.RootElement.GetProperty("buildTopology").GetProperty("projects").EnumerateArray()
                .ToDictionary(project => project.GetProperty("path").GetString()!, StringComparer.Ordinal);
            foreach (var entry in bundle.Entries)
            {
                AssertProjectHash(entry.CaptureSha256, entry.Text, entry.Path + " (capture)");
                if (entry.OriginalSha256 is not null)
                {
                    Assert.Equal(originalProjects[entry.Path].GetProperty("sha256").GetString(), entry.OriginalSha256);
                    AssertProjectHash(entry.OriginalSha256, BeforeRecordedProjectAdditions(entry.Path, entry.Text), entry.Path + " (original)");
                    if (entry.Kind == "retired-project") Assert.True(originalProjects[entry.Path].GetProperty("inSolution").GetBoolean());
                }
                Assert.NotEmpty(entry.Provenance);
            }
            var supportText = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, "helper-support-history.json"));
            AssertProjectHash("48619b9b589bb15dcee37592cfc710a236b28649edbfd38d27094248eb78dd2a", supportText, "helper-support-history.json");
            var support = JsonSerializer.Deserialize<RetirementCapture[]>(supportText, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(67, support.Length);
            var originalSources = baseline.RootElement.GetProperty("sourceRelocations").EnumerateArray()
                .ToDictionary(source => source.GetProperty("path").GetString()!, StringComparer.Ordinal);
            foreach (var entry in support)
            {
                Assert.Equal("retired-support", entry.Kind);
                AssertProjectHash(entry.CaptureSha256, entry.Text, entry.Path + " (helper support capture)");
                Assert.Equal(originalSources[entry.Path].GetProperty("sha256").GetString(), entry.OriginalSourceSha256);
            }
            return bundle.Entries.Concat(support).Concat(PackedProtocolConsumerCaptures).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, RetirementDisposition> ReadDispositions()
        {
            var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
            var text = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, "retirement-dispositions.json"));
            AssertProjectHash("4ac1b1842a59776620994c8114a8ad6b5646d04e8f7ce797e315e364b77d55a9", text, "retirement-dispositions.json");
            var entries = JsonSerializer.Deserialize<RetirementDisposition[]>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(Captures.Values.Where(entry => entry.Kind is "retired-source" or "retired-support").Select(entry => entry.Path).Order(StringComparer.Ordinal),
                entries.Select(entry => entry.Path).Order(StringComparer.Ordinal));
            return entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        }
    }

    private sealed record RetirementBundle(int SchemaVersion, string BaselineSha256, RetirementCapture[] Entries);
    internal sealed record RetirementCapture(string Path, string Kind, string Fixture, string CaptureSha256, string Provenance, string Text,
        string? OriginalSha256 = null, string? OriginalSourceSha256 = null, string? RawSha256 = null,
        bool? InFrozenProjectInventory = null, bool? InOriginalSolution = null);
    private sealed record RetirementDisposition(string Path, string Disposition, string[] Replacements, string[] RequiredTests);
}

using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentCompatibilityCandidateTestsRpcMigrationBaseline
{
    [Fact]
    public void PackedProtocolConsumerHistory_PreservesAuthenticOutOfSolutionEvidence()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var captures = AgentSourceMigrationProjectChanges.PackedProtocolConsumerCaptures;
        Assert.Equal(3, captures.Count);
        foreach (var capture in captures)
        {
            var text = AgentSourceMigrationProjectChanges.ReadMigrationSourceText(root, Path.Combine(root, capture.Path));
            Assert.Equal(capture.CaptureSha256, AgentSourceMigrationProjectChanges.ProjectTextHash(text));
            Assert.False(capture.InFrozenProjectInventory);
            Assert.False(capture.InOriginalSolution);
        }
        var project = XDocument.Parse(captures[0].Text);
        Assert.Equal("Exe", Assert.Single(project.Descendants("OutputType")).Value);
        var reference = Assert.Single(project.Descendants("PackageReference"));
        Assert.Equal("Sunder.Package.Agent.Protocol", (string?)reference.Attribute("Include"));
        Assert.Equal("$(AgentProtocolPackageVersion)", (string?)reference.Attribute("Version"));
        Assert.False(AgentSourceMigrationProjectChanges.HistoricalSolutionMembership(AgentSourceMigrationProjectChanges.PackedProtocolConsumerProject, false));
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, BaselineRelativePath)));
        var projects = baseline.RootElement.GetProperty("buildTopology").GetProperty("projects");
        Assert.Equal(57, projects.GetArrayLength());
        Assert.DoesNotContain(projects.EnumerateArray(), value => value.GetProperty("path").GetString() == AgentSourceMigrationProjectChanges.PackedProtocolConsumerProject);
    }

    [Fact]
    public void RetirementDeletionInventory_ContainsExactly26ProjectsAndAuthenticatedFiles()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var text = AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(Path.Combine(root,
            AgentSourceMigrationProjectChanges.PreCanonicalProjectsDirectory, "deletion-inventory.json"));
        Assert.Equal("832e64c7634678c9c17d24385c1e054f2fc15f2353dcd79c3984111e692091a8", AgentSourceMigrationProjectChanges.ProjectTextHash(text));
        using var inventory = JsonDocument.Parse(text);
        var value = inventory.RootElement;
        Assert.Equal(1, value.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(57, value.GetProperty("frozenRpcProjectCount").GetInt32());
        var captures = AgentSourceMigrationProjectChanges.RetirementCaptures.Values
            .Where(capture => capture.Kind.StartsWith("retired-", StringComparison.Ordinal)).ToDictionary(capture => capture.Path, StringComparer.Ordinal);
        var entries = value.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(249, entries.Length);
        Assert.Equal(captures.Keys.Order(StringComparer.Ordinal), entries.Select(entry => entry.GetProperty("path").GetString()).Order(StringComparer.Ordinal));
        foreach (var entry in entries)
        {
            var capture = captures[entry.GetProperty("path").GetString()!];
            Assert.Equal(capture.Kind, entry.GetProperty("kind").GetString());
            Assert.Equal(capture.CaptureSha256, entry.GetProperty("sha256").GetString());
            var captureFile = capture.Kind.StartsWith("retired-fixture-", StringComparison.Ordinal) ? "packed-protocol-consumer-history.json"
                : capture.Kind == "retired-support" ? "helper-support-history.json" : "retirement-history.json";
            Assert.Equal(captureFile, entry.GetProperty("captureFile").GetString());
            if (capture.OriginalSha256 is not null) Assert.Equal(capture.OriginalSha256, entry.GetProperty("originalProjectSha256").GetString());
            if (capture.OriginalSourceSha256 is not null) Assert.Equal(capture.OriginalSourceSha256, entry.GetProperty("originalSourceSha256").GetString());
            if (capture.Kind is "retired-project" or "retired-fixture-project")
            {
                Assert.Equal(capture.Kind == "retired-project", entry.GetProperty("inFrozenProjectInventory").GetBoolean());
                Assert.Equal(capture.Kind == "retired-project", entry.GetProperty("inOriginalSolution").GetBoolean());
            }
        }
        var projects = value.GetProperty("projects").EnumerateArray().Select(project => project.GetString()!).ToArray();
        Assert.Equal(26, projects.Length);
        Assert.Equal(captures.Values.Where(capture => capture.Kind is "retired-project" or "retired-fixture-project")
            .Select(capture => capture.Path).Order(StringComparer.Ordinal), projects);
        Assert.Contains(AgentSourceMigrationProjectChanges.PackedProtocolConsumerProject, projects);
        const string retained = "src/Sunder.Agent.Execution.Common/Sunder.Agent.Execution.Common.csproj";
        Assert.DoesNotContain(retained, captures.Keys);
        Assert.Equal(retained, Assert.Single(value.GetProperty("retainedProjects").EnumerateArray()).GetString());
        var counts = value.GetProperty("counts");
        foreach (var (key, count) in new[] { ("projects", 26), ("productionWrappers", 154), ("helperSupportFiles", 67), ("fixtureSources", 1), ("obsoleteTools", 1), ("totalFiles", 249) })
            Assert.Equal(count, counts.GetProperty(key).GetInt32());
    }
}

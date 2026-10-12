using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // These six current test inventories changed during the natural-service port.
    // The frozen migration reader consumes their authenticated former declarations
    // as data, independently of current tests and without compiling captured code.
    internal static IReadOnlyList<string> ReadHistoricalCandidateSources(string name)
        => CandidateInventoryHistory.Value[name].Paths;

    private static IReadOnlyDictionary<string, CandidateInventory> ReadCandidateInventories()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string fixture = "NaturalServices.candidate-inventories.json";
        var text = ReadNormalizedProjectText(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures", fixture));
        AssertProjectHash("fef3ce50781ae8963dd59be793235e1b67a34dd1fe6b5479d923dbdd940714f2", text, fixture);
        var history = JsonSerializer.Deserialize<CandidateInventoryBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(new[]
        {
            "AgentCanonicalSourceMigrationTests.EmbeddingSources",
            "AgentCanonicalSourceMigrationTests.ToolProfileSources",
            "DockerPresentationSourceMigrationTests.SourcePaths",
            "LocalPresentationSourceMigrationTests.SourcePaths",
            "McpManagementSourceMigrationTests.SourcePaths",
            "SkillsManagementSourceMigrationTests.SourcePaths",
        }, history.Inventories.Select(inventory => inventory.Name).Order(StringComparer.Ordinal));
        Assert.Equal(17, history.Inventories.Sum(inventory => inventory.Paths.Length));
        foreach (var inventory in history.Inventories)
        {
            Assert.Equal("git-index-declaration", inventory.Provenance);
            Assert.Matches("^[0-9a-f]{40}$", inventory.IndexBlob);
            Assert.Matches("^[0-9a-f]{64}$", inventory.SourceSha256);
            var separator = inventory.Name.IndexOf('.');
            Assert.Equal("tests/Sunder.Package.Agent.Tests/" + inventory.Name[..separator] + ".cs", inventory.SourcePath);
            Assert.StartsWith("    internal static readonly string[] " + inventory.Name[(separator + 1)..] + " =", inventory.Declaration, StringComparison.Ordinal);
            AssertProjectHash(inventory.DeclarationSha256, inventory.Declaration, inventory.Name + " (captured declaration)");
            var declared = Regex.Matches(inventory.Declaration, "\"([^\"\\n]+)\"")
                .Select(match => match.Groups[1].Value)
                .Select(value => value.StartsWith("src/", StringComparison.Ordinal)
                    ? value : "src/Sunder.Package.Agent/Rpc/Consumers/" + value);
            Assert.Equal(declared, inventory.Paths);
        }
        return history.Inventories.ToDictionary(inventory => inventory.Name, StringComparer.Ordinal);
    }

    private sealed record CandidateInventoryBundle(int SchemaVersion, CandidateInventory[] Inventories);
    private sealed record CandidateInventory(string Name, string SourcePath, string IndexBlob, string SourceSha256,
        string Provenance, string Declaration, string DeclarationSha256, string[] Paths);

    private static class CandidateInventoryHistory
    {
        internal static readonly IReadOnlyDictionary<string, CandidateInventory> Value = ReadCandidateInventories();
    }
}

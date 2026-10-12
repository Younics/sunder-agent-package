using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static IReadOnlyDictionary<string, string> CentralSemanticRelocations { get; } = ReadCentralSemanticRelocations();

    private static IReadOnlyDictionary<string, string> ReadCentralSemanticRelocations()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var directory = Path.Combine(root, "src/Sunder.Package.Agent/Models");
        var project = ReadSemanticSourceMapHistory(root);
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        const string prefix = "$(MSBuildThisFileDirectory)";
        foreach (var source in project.Descendants("SunderAgentSemanticSource"))
        {
            var include = source.Attribute("Include")!.Value;
            Assert.StartsWith(prefix, include, StringComparison.Ordinal);
            Assert.DoesNotContain('*', include);
            var path = Path.GetFullPath(Path.Combine(directory, include[prefix.Length..].Replace('\\', '/')));
            var original = "src/Sunder.Package.Agent.Contracts/" + source.Attribute("Link")!.Value;
            Assert.True(actual.TryAdd(original, Path.GetRelativePath(root, path).Replace('\\', '/')));
            if (!File.Exists(path))
            {
                // The six retired natural-service helpers are historical data, not
                // compilable source. Preserve their reviewed semantic destination
                // only when the captured authenticated before-image is available.
                Assert.True(TryReadNaturalServiceHistory(root, path, out _),
                    $"Relocated semantic source is missing without captured history: {path}");
            }
            Assert.False(File.Exists(Path.Combine(root, original)), $"Both copies of semantic source remain: {original}");
        }
        // Assertions alone consult the immutable disposition. Builds consume only
        // the explicit owning source items, never this migration oracle.
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")));
        var expected = oracle.RootElement.GetProperty("sourceRelocations").EnumerateArray()
            .Where(source => source.GetProperty("boundary").GetString() == "central"
                && source.GetProperty("disposition").GetString() is "migrate-base-agent-semantic-source" or "migrate-business-source")
            .ToDictionary(source => source.GetProperty("path").GetString()!, source => source.GetProperty("destination").GetString()!, StringComparer.Ordinal);
        Assert.Equal(142, expected.Count);
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        return actual;
    }

    internal static XDocument ReadSemanticSourceMapHistory(string root)
    {
        var text = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, "AgentSemanticSources.props.txt"));
        AssertProjectHash("c37ed587176e9a90c250d2b59043966d537b64173ca89face132a4f5d5f7594f", text, "historical AgentSemanticSources.props");
        return XDocument.Parse(text);
    }
}

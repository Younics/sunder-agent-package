using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Exact current paths absent from the authenticated pre-natural Git inputs.
    // This affects the historical view only; unknown additions remain visible and
    // any edit/removal of an admitted current source fails its guard.
    private static bool IsNaturalServiceSourceAddition(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!NaturalServiceAdditionHistory.Value.TryGetValue(relative, out var addition)) return false;
        AssertProjectHash(addition.CurrentSha256, BeforeApprovedSourceSplits(relative, ReadNormalizedProjectText(path)), relative + " (post-natural source addition)");
        return true;
    }

    private static IReadOnlyDictionary<string, NaturalServiceAddition> ReadNaturalServiceAdditions()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string fixture = "NaturalServices.source-additions.json";
        var text = ReadNormalizedProjectText(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures", fixture));
        AssertProjectHash("512048f70639b65b0110e0438575fda5f6e755a27caf06bcf4de2d3053a4229c", text, fixture);
        var history = JsonSerializer.Deserialize<NaturalServiceAdditionBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(24, history.Sources.Length);
        AssertProjectHash("7780136f64531b505141bf83ac169af566483dacc19008665f12b7e089aed0c0",
            string.Join("\n", history.Sources.Select(source => source.Path).Order(StringComparer.Ordinal)) + "\n",
            "post-natural added source inventory");
        foreach (var source in history.Sources)
        {
            Assert.Equal("git-head-and-index-absence", source.Provenance);
            Assert.Equal("1ca6dcf7cf85e41bd93292c7833ad53eb48bccaa", source.HeadCommit);
            Assert.StartsWith("src/Sunder.Package.Agent", source.Path, StringComparison.Ordinal);
            Assert.EndsWith(".cs", source.Path, StringComparison.Ordinal);
            var path = Path.Combine(root, source.Path);
            Assert.True(File.Exists(path), $"Post-natural source addition is missing: {source.Path}");
            AssertProjectHash(source.CurrentSha256, BeforeApprovedSourceSplits(source.Path, ReadNormalizedProjectText(path)), source.Path + " (post-natural source addition)");
        }
        return history.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    }

    private sealed record NaturalServiceAdditionBundle(int SchemaVersion, NaturalServiceAddition[] Sources);
    private sealed record NaturalServiceAddition(string Path, string CurrentSha256, string HeadCommit, string Provenance);

    private static class NaturalServiceAdditionHistory
    {
        internal static readonly IReadOnlyDictionary<string, NaturalServiceAddition> Value = ReadNaturalServiceAdditions();
    }
}

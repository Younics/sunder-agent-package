using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // An outer current-source boundary, before the existing frozen inverses.
    // The approved member relocation does not replace any canonical before-image.
    private static string BeforeApprovedSourceSplits(string relative, string current)
    {
        current = BeforeApprovedUnnamedProviderContent(relative, current);
        current = BeforeApprovedDomainBoundary(relative, current);
        // Separately approved Core response-retirement handoff: its verifier proves
        // that removing only this ChangesAsync argument restores the qualified source.
        if (relative == "src/Sunder.Package.Agent.Subagents/Runtime/SubagentManagementService.cs")
        {
            AssertProjectHash("eabfe71a5a64d7770aaeaadcfbdb79bbf56239db45d11e228317daad860a2a47", current,
                relative + " (approved ChangesAsync response-retirement opt-in)");
            const string argument = ", CompleteOnResponseContentRetirement = true";
            Assert.Equal(2, current.Split(argument, StringSplitOptions.None).Length);
            var previous = current.Replace(argument, "", StringComparison.Ordinal);
            AssertProjectHash("887ffed145468ae1f0f497aecff7a344b81b5eb806981c0be16a72ba229ba029", previous,
                relative + " (preceding qualified source)");
            return previous;
        }
        if (!ApprovedSourceSplitHistory.Value.TryGetValue(relative, out var source)) return current;
        AssertProjectHash(source.AfterSha256, current, relative + " (approved source split current boundary)");
        Assert.NotNull(source.BeforeSha256);
        var before = string.Concat(source.BeforeLines);
        AssertProjectHash(source.BeforeSha256!, before, relative + " (approved source split before-image)");
        return before;
    }

    private static bool IsApprovedSourceSplitAddition(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!ApprovedSourceSplitHistory.Value.TryGetValue(relative, out var source) || source.BeforeSha256 is not null)
            return false;
        AssertProjectHash(source.AfterSha256, ReadNormalizedProjectText(path), relative + " (approved source split addition)");
        return true;
    }

    private static IReadOnlyDictionary<string, ApprovedSourceSplit> ReadApprovedSourceSplits()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string fixture = "NaturalServices.approved-source-splits.json";
        var text = ReadNormalizedProjectText(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures", fixture));
        AssertProjectHash("17d48a84315775e6aa3cbe590938abde1a1adc27e4d046087a11d033e4ee07b2", text, fixture);
        var history = JsonSerializer.Deserialize<ApprovedSourceSplitBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(338, history.MembersCompared);
        Assert.Equal(7, history.Sources.Length);
        foreach (var source in history.Sources)
        {
            AssertProjectHash(source.AfterSha256, BeforeApprovedDomainBoundary(source.Path,
                    BeforeApprovedUnnamedProviderContent(source.Path, ReadNormalizedProjectText(Path.Combine(root, source.Path)))),
                source.Path + " (approved source split current boundary)");
            if (source.BeforeSha256 is null) Assert.Empty(source.BeforeLines);
            else AssertProjectHash(source.BeforeSha256, string.Concat(source.BeforeLines), source.Path + " (approved before-image)");
        }
        return history.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    }

    private sealed record ApprovedSourceSplitBundle(int SchemaVersion, int MembersCompared, ApprovedSourceSplit[] Sources);
    private sealed record ApprovedSourceSplit(string Path, string? BeforeSha256, string AfterSha256, string[] BeforeLines);

    private static class ApprovedSourceSplitHistory
    {
        internal static readonly IReadOnlyDictionary<string, ApprovedSourceSplit> Value = ReadApprovedSourceSplits();
    }
}

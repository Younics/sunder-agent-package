using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private static string BeforeApprovedDomainBoundary(string relative, string current)
    {
        current = BeforeApprovedFiniteConstraints(relative, current);
        current = BeforeApprovedBudgetCatalog(relative, current);
        if (!ApprovedDomainBoundaryHistory.Value.TryGetValue(relative, out var source)) return current;
        AssertProjectHash(source.AfterSha256, current, relative + " (approved optional/domain current boundary)");
        var before = string.Concat(source.BeforeLines);
        AssertProjectHash(source.BeforeSha256, before, relative + " (approved optional/domain before-image)");
        return before;
    }

    private static IReadOnlyDictionary<string, ApprovedDomainBoundary> ReadApprovedDomainBoundary()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string fixture = "NaturalServices.approved-domain-boundary.json";
        var text = ReadNormalizedProjectText(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures", fixture));
        AssertProjectHash("ff94ee74f40734fbca20662100b01235ec0298958cfe4295285117013db54f21", text, fixture);
        var history = JsonSerializer.Deserialize<ApprovedDomainBoundaryBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(10, history.Sources.Length);
        foreach (var source in history.Sources)
        {
            AssertProjectHash(source.AfterSha256,
                BeforeApprovedFiniteConstraints(source.Path, ReadNormalizedProjectText(Path.Combine(root, source.Path))), source.Path);
            AssertProjectHash(source.BeforeSha256, string.Concat(source.BeforeLines), source.Path + " (approved before-image)");
        }
        return history.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    }

    private sealed record ApprovedDomainBoundaryBundle(int SchemaVersion, ApprovedDomainBoundary[] Sources);
    private sealed record ApprovedDomainBoundary(string Path, string BeforeSha256, string AfterSha256, string[] BeforeLines);
    private static class ApprovedDomainBoundaryHistory
    {
        internal static readonly IReadOnlyDictionary<string, ApprovedDomainBoundary> Value = ReadApprovedDomainBoundary();
    }
}

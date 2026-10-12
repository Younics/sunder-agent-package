using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentCompatibilityCandidateTestsRpcMigrationBaseline
{
    [Fact]
    public void SemanticSourceMap_IsHistoricalDataAndAll142RelocationsHaveReviewedDisposition()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        Assert.False(File.Exists(Path.Combine(root, "src/Sunder.Package.Agent/Models/AgentSemanticSources.props")));
        Assert.Equal(142, AgentSourceMigrationProjectChanges.ReadSemanticSourceMapHistory(root).Descendants("SunderAgentSemanticSource").Count());
        Assert.Equal(142, AgentSourceMigrationProjectChanges.CentralSemanticRelocations.Count);
        var retired = new[]
        {
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentRpcCatalog.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentRpcCatalog.Async.cs",
            "src/Sunder.Package.Agent/Models/Extensions/AgentProfileSelectableCapabilityChangeObserver.cs",
        };
        Assert.All(retired, relative => Assert.Contains(relative, AgentSourceMigrationProjectChanges.CentralSemanticRelocations.Values));
        Assert.All(AgentSourceMigrationProjectChanges.CentralSemanticRelocations.Values, relative =>
        {
            var path = Path.Combine(root, relative);
            if (retired.Contains(relative, StringComparer.Ordinal))
            {
                Assert.False(File.Exists(path), $"Retired catalog helper reintroduced: {relative}");
                Assert.True(AgentSourceMigrationProjectChanges.TryReadNaturalServiceHistory(root, path, out var history));
                Assert.NotEmpty(history);
            }
            else Assert.True(File.Exists(path), $"Missing actual relocated business source: {relative}");
        });
    }

    [Fact]
    public void PhysicalCleanup_ChangesOnlyRetiredExclusionsAndLegacyBuildMachinery()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var changes = AgentSourceMigrationProjectChanges.ReadPhysicalCleanupHistory(root);
        Assert.Equal(16, changes.Count);
        Assert.Equal(AgentSourceMigrationProjectChanges.CanonicalProjectHistories.Select(project => project.Project).Append("Directory.Build.targets").Order(StringComparer.Ordinal),
            changes.Select(change => change.Path).Order(StringComparer.Ordinal));
        foreach (var change in changes)
        {
            var current = AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(Path.Combine(root, change.Path));
            // Compare this cleanup phase with its authenticated post-cleanup input,
            // after guarding and undoing the later natural-service project changes.
            if (AgentSourceMigrationProjectChanges.TryReadNaturalServiceDelta(root, Path.Combine(root, change.Path), current, out var preNatural))
                current = preNatural;
            Assert.Equal(change.AfterSha256, AgentSourceMigrationProjectChanges.ProjectTextHash(current));
            var expected = XDocument.Parse(change.BeforeText);
            if (change.Path.EndsWith(".csproj", StringComparison.Ordinal))
            {
                var exclusions = expected.Descendants().Where(item => item.Name is { LocalName: "Compile" or "None" } && item.Attribute("Remove") is not null).ToArray();
                Assert.NotEmpty(exclusions);
                Assert.All(exclusions, item => Assert.Single(item.Attributes()));
                exclusions.Remove();
                expected.Descendants("ItemGroup").Where(group => !group.HasElements).Remove();
            }
            else
            {
                expected.Root!.Elements("ItemGroup").Where(group => group.Elements().Any(item =>
                    item.Name is { LocalName: "SunderContractBundle" or "SunderUsesContract" or "SunderRpcProvider" }
                    || (string?)item.Attribute("Include") == "$(SunderPackageAppProject)")).Remove();
                expected.Root.Elements("PropertyGroup").Where(group => (string?)group.Attribute("Condition") is
                    "'$(SunderPackageRole)' == 'app'" or "'$(SunderPackageRole)' == 'protocol'").Remove();
                var obsoleteErrors = expected.Descendants("Error").Where(error =>
                    ((string?)error.Attribute("Condition"))?.StartsWith("'$(SunderPackageRole)' == 'worker'", StringComparison.Ordinal) == true
                    || (string?)error.Attribute("Condition") == "'$(SunderPackageRole)' == 'protocol' and '$(SunderPackageProject)' != 'false'").ToArray();
                Assert.Equal(4, obsoleteErrors.Length);
                obsoleteErrors.Remove();
                var roleGuard = Assert.Single(expected.Descendants("Error"), error =>
                    ((string?)error.Attribute("Text"))?.StartsWith("SunderPackageRole must be worker, app, protocol, or test", StringComparison.Ordinal) == true);
                roleGuard.SetAttributeValue("Condition", "'$(SunderPackageRole)' != '' and '$(SunderPackageRole)' != 'test'");
                roleGuard.SetAttributeValue("Text", "SunderPackageRole must be unset for canonical package projects or 'test' for test projects, but was '$(SunderPackageRole)'.");
                var stacks = Assert.Single(expected.Descendants("PackageReference"), reference => (string?)reference.Attribute("Include") == "Sunder.Sdk.Stacks");
                Assert.Null(stacks.Attribute("ExcludeAssets"));
                stacks.SetAttributeValue("ExcludeAssets", "buildTransitive");
            }
            Assert.Equal(AgentSourceMigrationProjectChanges.ProjectSurfaceSignature(expected.ToString(SaveOptions.DisableFormatting)),
                AgentSourceMigrationProjectChanges.ProjectSurfaceSignature(current));
        }
        // Removing broad exclusions is safe only while every retired input stays absent.
        foreach (var capture in AgentSourceMigrationProjectChanges.RetirementCaptures.Values.Where(capture => capture.Kind.StartsWith("retired-", StringComparison.Ordinal)))
            Assert.False(File.Exists(Path.Combine(root, capture.Path)), $"Retired source/project reintroduced: {capture.Path}");
    }

    [Fact]
    public void UiFollowup_ExactInversesReconstructAuthenticatedBeforeImages()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var changes = AgentSourceMigrationProjectChanges.ReadUiFollowupHistory(root);
        Assert.Equal(2, changes.Count);
        foreach (var change in changes)
        {
            var path = Path.Combine(root, change.Path);
            var current = AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path);
            Assert.Equal(change.AfterSha256, AgentSourceMigrationProjectChanges.ProjectTextHash(current));
            var before = AgentSourceMigrationProjectChanges.BeforeUiFollowup(path, current);
            Assert.Equal(change.BeforeText, before);
            Assert.Equal(change.BeforeSha256, AgentSourceMigrationProjectChanges.ProjectTextHash(before));
        }
    }
}

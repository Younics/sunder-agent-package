using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentCompatibilityCandidateTestsRpcMigrationBaseline
{
    [Theory]
    [InlineData("src/Sunder.Package.Agent/Runtime/AgentAppRuntimeGateway.cs")]
    [InlineData("src/Sunder.Package.Agent/Runtime/AgentOwnerRuntimeProvider.cs")]
    [InlineData("src/Sunder.Package.Agent/Services/AgentProfileService.cs")]
    [InlineData("src/Sunder.Package.Agent/Services/AgentRuntimeCatalog.cs")]
    [InlineData("src/Sunder.Package.Agent/Services/AgentRunControlService.cs")]
    [InlineData("src/Sunder.Package.Agent/Services/AgentWorkspaceExecutionResolver.cs")]
    [InlineData("src/Sunder.Package.Agent/Services/AgentRunCoordinator.cs")]
    [InlineData("src/Sunder.Package.Agent.Provider.Anthropic/App/AnthropicSettingsViewModel.cs")]
    [InlineData("src/Sunder.Package.Agent.Provider.Gemini/App/GeminiSettingsViewModel.cs")]
    [InlineData("src/Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs")]
    public void OrdinarySourceHistory_RejectsUnreviewedChangesBeforeReconstructingHistory(string relative)
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var path = Path.Combine(root, relative);
        var current = AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path);
        Assert.NotEqual(current, AgentSourceMigrationProjectChanges.BeforeOrdinaryServices(root, path, current));
        Assert.ThrowsAny<XunitException>(() => AgentSourceMigrationProjectChanges.BeforeOrdinaryServices(root, path,
            current + "\n// Unreviewed source drift must not disappear behind historical reconstruction.\n"));
    }

    [Fact]
    public void RetirementHistory_ReconcilesAll57ProjectsWithCurrentRoleTopology()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        AgentSourceMigrationProjectChanges.AssertRetirementCurrentTopology(root);
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, BaselineRelativePath)));
        var projects = baseline.RootElement.GetProperty("buildTopology").GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(57, projects.Length);
        foreach (var project in projects)
        {
            var relative = project.GetProperty("path").GetString()!;
            var path = Path.Combine(root, relative);
            var original = AgentSourceMigrationProjectChanges.BeforeAdditions(root, path,
                File.Exists(path) ? AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path) : string.Empty);
            Assert.Equal(project.GetProperty("sha256").GetString(), AgentSourceMigrationProjectChanges.ProjectTextHash(original));
        }
    }

    [Fact]
    public void RetirementHistory_ReadsCapturedEvidenceWithoutProductionProjectsOrWrappers()
    {
        var repository = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        AgentSourceMigrationProjectChanges.AssertRetirementCurrentTopology(repository);
        var directory = Directory.CreateTempSubdirectory("sunder-history-retirement-");
        try
        {
            // A small input-only mirror: never copy outputs or any retiring source,
            // and never compile the captured legacy code. Use the real guarded reader.
            foreach (var relative in AgentSourceMigrationProjectChanges.RetirementValidationInputs(repository))
            {
                var path = Path.Combine(directory.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(repository, relative), path);
            }
            var captures = AgentSourceMigrationProjectChanges.RetirementCaptures.Values
                .Where(capture => capture.Kind.StartsWith("retired-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(249, captures.Length); // 26 projects, 154 wrappers, 67 support files, fixture Program and verifier.
            var solutionPath = Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx");
            var solution = XDocument.Load(solutionPath);
            solution.Descendants("Project").Where(project => captures.Any(capture => capture.Path == ((string?)project.Attribute("Path"))?.Replace('\\', '/'))).Remove();
            solution.Save(solutionPath);
            AgentSourceMigrationProjectChanges.AssertRetirementCurrentTopology(directory.FullName);
            foreach (var capture in captures)
            {
                var path = Path.Combine(directory.FullName, capture.Path);
                Assert.False(File.Exists(path));
                var read = capture.Kind is "retired-project" or "retired-fixture-project"
                    ? AgentSourceMigrationProjectChanges.ReadMsBuildHistory(directory.FullName, path)
                    : AgentSourceMigrationProjectChanges.ReadMigrationSourceText(directory.FullName, path);
                Assert.Equal(capture.Text, read);
                if (capture.Kind == "retired-project")
                    Assert.Equal(capture.OriginalSha256, AgentSourceMigrationProjectChanges.ProjectTextHash(
                        AgentSourceMigrationProjectChanges.BeforeAdditions(directory.FullName, path, string.Empty)));
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("<ItemGroup><ProjectReference Include=\"../../src/Sunder.Package.Agent/OwnerProtocol/Sunder.Package.Agent.OwnerProtocol.csproj\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Include=\"../../src/Sunder.Package.Agent/Program.cs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><PackageReference Include=\"Unexpected.Dependency\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><SunderPackageReferenceRole>worker</SunderPackageReferenceRole></PropertyGroup>")]
    public void RetainedProjectHistory_RejectsTopologyDriftBeforeReturningIndex(string addition)
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var path = Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj");
        var text = AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path);
        Assert.NotEqual(text, AgentSourceMigrationProjectChanges.BeforeAdditions(root, path, text));
        Assert.ThrowsAny<XunitException>(() => AgentSourceMigrationProjectChanges.BeforeAdditions(root, path,
            text.Replace("</Project>", addition + "\n</Project>", StringComparison.Ordinal)));
    }
}

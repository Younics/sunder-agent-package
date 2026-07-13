using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SolutionPackageInventoryTests
{
    [Fact]
    public void RuntimePackageProjects_AreInSolutionAndGeneratedManifestCoverage()
    {
        var runtimePackages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        var solutionProjectPaths = AgentPackageRepositoryInventory.GetSolutionProjectPaths();
        var manifestCoveragePaths = GeneratedPackageManifestTests.CoveredProjectPaths;

        Assert.NotEmpty(runtimePackages);
        Assert.Equal(
            runtimePackages.Count,
            runtimePackages.Select(static package => package.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var package in runtimePackages)
        {
            Assert.Contains(package.ProjectPath, solutionProjectPaths);
            Assert.Contains(package.ProjectPath, manifestCoveragePaths);
        }

        Assert.True(
            manifestCoveragePaths.SetEquals(runtimePackages.Select(static package => package.ProjectPath)),
            "Generated manifest coverage must exactly match the PackageMetadata runtime project inventory.");
    }

    [Fact]
    public void TestProjects_AreInSolutionUnlessExplicitlySupportOnly()
    {
        var testsRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "tests");
        var solutionProjects = AgentPackageRepositoryInventory.GetSolutionProjectPaths();
        var testProjects = Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(project => !SupportOnlyTestProjects.Contains(NormalizeRepositoryPath(project)))
            .ToArray();

        Assert.NotEmpty(testProjects);
        foreach (var testProject in testProjects)
        {
            Assert.Contains(Path.GetFullPath(testProject), solutionProjects);
        }
    }

    [Fact]
    public void PackageModules_MatchRuntimePackageInventoryAndCompositionCoverage()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var moduleProjects = Directory.EnumerateFiles(sourceRoot, "PackageModule.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(static module => Path.GetDirectoryName(module)!)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeProjects = AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Select(static package => package.ProjectPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(moduleProjects);
        Assert.True(
            moduleProjects.SetEquals(runtimeProjects),
            "Every PackageModule must belong to the runtime package inventory exercised by PackageModuleCompositionTests.");
    }

    [Fact]
    public void RuntimePackages_ExclusivelyOptIntoCentralPackageBuildMode()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var runtimeProjects = AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Select(static package => package.ProjectPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allProjects = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();

        foreach (var projectPath in allProjects)
        {
            var projectText = File.ReadAllText(projectPath);
            var optsIntoPackageBuild = projectText.Contains(
                "<SunderPackageProject>true</SunderPackageProject>",
                StringComparison.Ordinal);
            Assert.Equal(runtimeProjects.Contains(Path.GetFullPath(projectPath)), optsIntoPackageBuild);
            Assert.DoesNotContain("sunder-core", projectText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Build", projectText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReleaseWorkflowInventory_MatchesRuntimePackages()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "sunder-package-release.yml");
        var workflow = File.ReadAllText(workflowPath);
        var expected = AgentPackageRepositoryInventory.GetRuntimePackageProjects().ToDictionary(
            static package => GetReleaseKey(package.Name),
            package => NormalizeRepositoryPath(package.ProjectPath),
            StringComparer.Ordinal);
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "packages.json")));
        var releaseProjects = inventory.RootElement.GetProperty("packages").EnumerateArray()
            .Where(static package => package.GetProperty("artifactType").GetString() == "sunderpkg")
            .ToDictionary(
                static package => package.GetProperty("key").GetString()!,
                static package => package.GetProperty("projectPath").GetString()!,
                StringComparer.Ordinal);

        Assert.Contains("- \"agent*/v*\"", workflow, StringComparison.Ordinal);
        Assert.Contains("select(.key == $key and .artifactType == \"sunderpkg\")", workflow, StringComparison.Ordinal);
        Assert.Equal(expected.Count, releaseProjects.Count);
        foreach (var (releaseKey, projectPath) in expected)
        {
            Assert.True(releaseProjects.TryGetValue(releaseKey, out var releaseProject));
            Assert.Equal(projectPath, releaseProject);
        }
    }

    private static readonly IReadOnlySet<string> SupportOnlyTestProjects = new HashSet<string>(
        ["tests/Sunder.Package.Agent.Provider.TestSupport/Sunder.Package.Agent.Provider.TestSupport.csproj"],
        StringComparer.OrdinalIgnoreCase);

    private static string GetReleaseKey(string packageName)
    {
        const string prefix = "Sunder.Package.Agent";
        var suffix = packageName[prefix.Length..].TrimStart('.');
        return string.IsNullOrEmpty(suffix)
            ? "agent"
            : "agent-" + suffix.Replace('.', '-').ToLowerInvariant();
    }

    private static string NormalizeRepositoryPath(string path)
        => Path.GetRelativePath(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path).Replace('\\', '/');
}

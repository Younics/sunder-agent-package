using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class CurrentCompatibilityTests
{
    [Fact]
    public void SourceInventory_ExclusionsAreRepositoryRelativeAndRetainProtectedBoundaries()
    {
        foreach (var root in new[]
                 {
                     AgentPackageRepositoryInventory.RepositoryRoot.FullName,
                     Path.Combine(Path.GetTempPath(), "artifacts", "bin", "obj", "compatibility", "checkout"),
                 })
        {
            Assert.True(AgentPackageRepositoryInventory.IsSourceFile(Path.Combine(root, "src", "Package", "Source.cs"), root));
            Assert.True(AgentPackageRepositoryInventory.IsSourceFile(Path.Combine(root, "src", "Package", "binary", "Source.cs"), root));
            foreach (var relative in new[]
                     {
                         "src/Package/bin/Generated.cs", "src/Package/obj/Generated.cs",
                         "artifacts/Source.cs", "compatibility/Source.cs",
                         "tests/Fixtures/PreCanonicalProjects/Source.cs", "../sibling/Source.cs",
                     })
                Assert.False(AgentPackageRepositoryInventory.IsSourceFile(
                    Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))), root));
        }
    }

    [Fact]
    public void CurrentTopology_HasFifteenCanonicalProjectsAndExplicitGeneratedRoleReferences()
    {
        var root = RegressionTestRpcDescriptors.RepositoryRoot;
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "packages.json")));
        CurrentContractInventory.AssertPackageInventory(inventory.RootElement);
        var solution = XDocument.Load(Path.Combine(root, "Sunder.AgentPackage.slnx"));
        var expectedProjects = CurrentContractInventory.Packages.Select(value => value.ProjectPath)
            .Append("src/Sunder.Agent.Execution.Common/Sunder.Agent.Execution.Common.csproj").Order(StringComparer.Ordinal);
        Assert.Equal(expectedProjects, Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
        foreach (var entry in solution.Descendants("Project"))
            Assert.True(File.Exists(Path.Combine(root, entry.Attribute("Path")!.Value.Replace('\\', '/'))), $"Stale solution entry: {entry}");
        foreach (var package in CurrentContractInventory.Packages)
        {
            Assert.Single(solution.Descendants("Project"), value => value.Attribute("Path")!.Value.Replace('\\', '/') == package.ProjectPath);
            var project = XDocument.Load(Path.Combine(root, package.ProjectPath));
            Assert.Equal("Microsoft.NET.Sdk", project.Root!.Attribute("Sdk")!.Value);
            Assert.Equal(package.PackageId, Assert.Single(project.Descendants("SunderPackageFamilyId")).Value);
            Assert.Equal(package.HasApp ? new[] { "true" } : [], project.Descendants("SunderSdkAvaloniaReference").Select(value => value.Value));
            Assert.Contains($"Id = \"{package.PackageId}\"", File.ReadAllText(Path.Combine(root, package.SourceDirectory, "PackageMetadata.cs")), StringComparison.Ordinal);
            foreach (var name in new[] { "SunderPackageRole", "SunderPackageAppProject", "SunderPackagePrimaryProject", "SunderRpcBinding", "SunderRpcProvider", "SunderUsesContract", "SunderContractBundle", "Target", "ProjectReference" })
                Assert.Empty(project.Descendants(name));
            var excludedPresentation = package.Key switch
            {
                "agent-builder" or "agent-execution-docker" or "agent-execution-local" or "agent-memory-semantic" or "agent-skills"
                    => new[] { "CapabilitySelectionState.cs", "ModelBindingEditorState.cs", "ProviderModelLoader.cs", "AgentToolPresentationMarkdown.cs" },
                "agent-mcp" => ["CapabilitySelectionState.cs", "ModelBindingEditorState.cs", "ProviderModelLoader.cs"],
                _ => [],
            };
            Assert.Equal(excludedPresentation.Select(name => "../Sunder.Package.Agent.Shared/Presentation/" + name).Order(StringComparer.Ordinal),
                project.Descendants("Compile").SelectMany(item => (item.Attribute("Remove")?.Value ?? "").Replace('\\', '/')
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)).Order(StringComparer.Ordinal));
            var defaults = package.Key == "agent" ? project : XDocument.Load(Path.Combine(root, "Sunder.Agent.Worker.props"));
            Assert.Equal("true", Assert.Single(defaults.Descendants("SunderPackageProject")).Value);
            Assert.Equal("true", Assert.Single(defaults.Descendants("SunderSdkWorkerReference")).Value);
            foreach (var name in new[] { "SelfContained", "UseAppHost", "PublishSingleFile", "PublishTrimmed", "EnableRuntimePackDownload", "EnableAppHostPackDownload" })
                Assert.Equal("false", Assert.Single(defaults.Descendants(name)).Value);
            Assert.Equal("win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64", Assert.Single(defaults.Descendants("RuntimeIdentifiers")).Value);
            Assert.All(defaults.Descendants("OutputType"), value => Assert.Equal("Library", value.Value));
            foreach (var directory in new[] { "src", package.SourceDirectory })
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                    Assert.False(File.Exists(Path.Combine(root, directory, name)), $"Unexpected nested build override: {directory}/{name}");
        }
        foreach (var path in new[] { "src", "tests" }.SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories))
                     .Where(AgentPackageRepositoryInventory.IsSourceFile).Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets"))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("PreCanonicalProjects", text, StringComparison.Ordinal);
            Assert.DoesNotContain("compatibility/identity-migration", text.Replace('\\', '/'), StringComparison.Ordinal);
            foreach (var reference in XDocument.Load(path).Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value.Replace('\\', '/');
                if (include.Contains("$(", StringComparison.Ordinal)) continue;
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include));
                Assert.True(File.Exists(target), $"Missing project reference: {target}");
                var package = CurrentContractInventory.Packages.SingleOrDefault(value => Path.Combine(root, value.ProjectPath) == target);
                if (package is null) continue;
                Assert.Equal("GetSunderPackageRoleReference", reference.Attribute("Targets")?.Value);
                Assert.Contains(reference.Attribute("AdditionalProperties")?.Value,
                    package.HasApp ? new[] { "SunderPackageReferenceRole=worker", "SunderPackageReferenceRole=app" } : ["SunderPackageReferenceRole=worker"]);
            }
        }
        var targets = XDocument.Load(Path.Combine(root, "Directory.Build.targets"));
        foreach (var name in new[] { "SunderContractBundle", "SunderUsesContract", "SunderRpcProvider", "SunderRpcBinding", "SunderPackageAppProject", "SunderPackagePrimaryProject" })
            Assert.Empty(targets.Descendants(name));
        Assert.Equal("buildTransitive", Assert.Single(targets.Descendants("PackageReference"), value => value.Attribute("Include")?.Value == "Sunder.Sdk.Stacks").Attribute("ExcludeAssets")?.Value);
    }

    [Fact]
    public void CurrentOutputs_MatchReviewedContractsProvidersPoliciesAuthoritiesAndCapabilities()
    {
        var root = RegressionTestRpcDescriptors.RepositoryRoot;
        Assert.Equal(31, CanonicalDescriptorInventory.Read().Count);
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "packages.json")));
        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects())
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(package.ManifestPath));
            CurrentPackagePolicyAssertions.AssertManifest(manifest.RootElement,
                path => File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(package.ManifestPath)!, "../payload/shared", path)), inventory.RootElement);
        }
    }
}

using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class ArchitectureReferenceTests
{
    [Fact]
    public void AgentExtensionPackages_DoNotReferenceBaseAgentImplementationProject()
    {
        var root = GetRepositoryRoot();
        var packagesRoot = Path.Combine(root.FullName, "src", "Packages");
        var extensionProjects = Directory.EnumerateFiles(packagesRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => Path.GetFileNameWithoutExtension(path).StartsWith("Sunder.Package.Agent.", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
            .Where(IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(extensionProjects);
        foreach (var projectPath in extensionProjects)
        {
            var normalizedProject = File.ReadAllText(projectPath).Replace('/', '\\');
            Assert.DoesNotContain("Sunder.Package.Agent\\Sunder.Package.Agent.csproj", normalizedProject, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AgentExtensionPackages_ReferenceAgentContractsProject()
    {
        var root = GetRepositoryRoot();
        var packagesRoot = Path.Combine(root.FullName, "src", "Packages");
        var extensionProjects = Directory.EnumerateFiles(packagesRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => Path.GetFileNameWithoutExtension(path).StartsWith("Sunder.Package.Agent.", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
            .Where(IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(extensionProjects);
        foreach (var projectPath in extensionProjects)
        {
            var normalizedProject = File.ReadAllText(projectPath).Replace('/', '\\');
            Assert.Contains("Sunder.Package.Agent.Contracts\\Sunder.Package.Agent.Contracts.csproj", normalizedProject, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AgentContracts_DoNotReferenceBaseAgentImplementationNamespaces()
    {
        var contractsRoot = Path.Combine(GetRepositoryRoot().FullName, "src", "Packages", "Sunder.Package.Agent.Contracts");
        var sourceFiles = Directory.EnumerateFiles(contractsRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
            .Where(IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile).Replace('/', '\\');
            Assert.DoesNotContain("Sunder.Package.Agent\\Sunder.Package.Agent.csproj", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent.PackageViews", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent.Services", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent.Storage", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SunderPackages_DoNotReferenceHostImplementationProjectsOrNamespaces()
    {
        var packagesRoot = Path.Combine(GetRepositoryRoot().FullName, "src", "Packages");
        var sourceFiles = EnumerateSourceFiles(packagesRoot, [".cs", ".csproj"]);

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile).Replace('/', '\\');
            Assert.DoesNotContain("Sunder.App", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Runtime.Host", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host\\Sunder.App", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host\\Sunder.Runtime.Host", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PackageProjects_DoNotKeepSourceManifests()
    {
        var packagesRoot = Path.Combine(GetRepositoryRoot().FullName, "src", "Packages");
        var manifestPaths = Directory.EnumerateFiles(packagesRoot, "sunder-package.json", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(manifestPaths);
    }

    [Fact]
    public void PackageProjects_DeclarePackageMetadataInCode()
    {
        var packagesRoot = Path.Combine(GetRepositoryRoot().FullName, "src", "Packages");
        var packageProjects = Directory.EnumerateFiles(packagesRoot, "Sunder.Package.*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
            .Where(IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(packageProjects);
        foreach (var projectPath in packageProjects)
        {
            var metadataPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "PackageMetadata.cs");
            Assert.True(File.Exists(metadataPath), $"Package project '{projectPath}' must declare PackageMetadata.cs.");
            Assert.Contains("SunderPackage(", File.ReadAllText(metadataPath), StringComparison.Ordinal);
        }
    }

    private static DirectoryInfo GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "Packages")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static bool IsSourceFile(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        return !normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               && !normalizedPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] EnumerateSourceFiles(string root, IReadOnlyCollection<string> extensions)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

using System.Xml.Linq;

namespace Sunder.Package.Agent.Tests;

internal sealed record RuntimePackageProject(
    string Name,
    string ProjectPath,
    string MetadataPath)
{
    public string DirectoryPath => Path.GetDirectoryName(ProjectPath)!;

    public bool IsWorker => XDocument.Load(ProjectPath)
        .Descendants("SunderPackageTarget")
        .Any(static target => string.Equals(
            target.Attribute("Kind")?.Value,
            "worker",
            StringComparison.Ordinal));
}

internal static class AgentPackageRepositoryInventory
{
    private static readonly Lazy<DirectoryInfo> RepositoryRootValue = new(FindRepositoryRoot);

    public static DirectoryInfo RepositoryRoot => RepositoryRootValue.Value;

    public static IReadOnlyList<RuntimePackageProject> GetRuntimePackageProjects()
    {
        var sourceRoot = Path.Combine(RepositoryRoot.FullName, "src");
        return Directory.EnumerateFiles(sourceRoot, "PackageMetadata.cs", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .Select(CreateRuntimePackageProject)
            .OrderBy(static project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlySet<string> GetSolutionProjectPaths()
    {
        var solutionPath = Path.Combine(RepositoryRoot.FullName, "Sunder.AgentPackage.slnx");
        var solution = XDocument.Load(solutionPath);
        return solution.Descendants("Project")
            .Select(static element => element.Attribute("Path")?.Value)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(RepositoryRoot.FullName, NormalizePath(path!))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSourceFile(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        return !normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               && !normalizedPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimePackageProject CreateRuntimePackageProject(string metadataPath)
    {
        var packageDirectory = Path.GetDirectoryName(metadataPath)!;
        var projectPaths = Directory.EnumerateFiles(packageDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            .ToArray();

        if (projectPaths.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one project next to runtime package metadata '{metadataPath}', found {projectPaths.Length}.");
        }

        var projectPath = Path.GetFullPath(projectPaths[0]);
        return new RuntimePackageProject(Path.GetFileNameWithoutExtension(projectPath), projectPath, metadataPath);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var startPaths = new[]
            {
                Environment.GetEnvironmentVariable("SUNDER_AGENT_REPOSITORY_ROOT"),
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var startPath in startPaths)
        {
            for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx"))
                    && Directory.Exists(Path.Combine(directory.FullName, "src", "Sunder.Package.Agent")))
                {
                    return directory;
                }
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from SUNDER_AGENT_REPOSITORY_ROOT, the test output, or the working directory.");
    }

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}

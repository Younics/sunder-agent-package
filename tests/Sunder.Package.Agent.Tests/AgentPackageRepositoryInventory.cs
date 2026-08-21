using System.Xml.Linq;

namespace Sunder.Package.Agent.Tests;

internal sealed record RuntimePackageProject(
    string Name,
    string ProjectPath,
    string MetadataPath,
    string? AppProjectPath,
    IReadOnlyList<string> ProtocolProjectPaths)
{
    public string DirectoryPath => Path.GetDirectoryName(ProjectPath)!;

    public string PackageDirectoryPath => Path.GetDirectoryName(MetadataPath)!;

    public string ProgramPath
    {
        get
        {
            var rootProgram = Path.Combine(DirectoryPath, "Program.cs");
            return File.Exists(rootProgram)
                ? rootProgram
                : Path.Combine(DirectoryPath, "Worker", "Program.cs");
        }
    }

    public string? FamilyId => GetProperty("SunderPackageFamilyId");

    public string? Role => GetProperty("SunderPackageRole");

    public bool IsWorker => string.Equals(Role, "worker", StringComparison.Ordinal);

    public string? AppAssemblyName => AppProjectPath is null
        ? null
        : GetAssemblyName(AppProjectPath);

    public IReadOnlyList<string> RoleProjectPaths =>
    [
        ProjectPath,
        .. AppProjectPath is null ? [] : new[] { AppProjectPath },
        .. ProtocolProjectPaths,
    ];

    private string? GetProperty(string name)
        => GetProjectProperty(ProjectPath, name);

    private static string GetAssemblyName(string projectPath)
        => GetProjectProperty(projectPath, "AssemblyName")
           ?? Path.GetFileNameWithoutExtension(projectPath);

    private static string? GetProjectProperty(string projectPath, string name)
        => XDocument.Load(projectPath)
            .Descendants(name)
            .Select(static property => property.Value)
            .SingleOrDefault();
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
        var projects = Directory.EnumerateFiles(packageDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .Select(static path => new RoleProject(
                Path.GetFullPath(path),
                XDocument.Load(path).Descendants("SunderPackageRole")
                    .Select(static property => property.Value)
                    .SingleOrDefault()))
            .ToArray();
        var worker = projects.Single(static project => string.Equals(project.Role, "worker", StringComparison.Ordinal));
        var app = projects.SingleOrDefault(static project => string.Equals(project.Role, "app", StringComparison.Ordinal));
        var protocols = projects
            .Where(static project => string.Equals(project.Role, "protocol", StringComparison.Ordinal))
            .Select(static project => project.Path)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var workerProject = XDocument.Load(worker.Path);
        var name = workerProject.Descendants("AssemblyName")
            .Select(static property => property.Value)
            .SingleOrDefault()
            ?? Path.GetFileNameWithoutExtension(worker.Path);
        return new RuntimePackageProject(
            name,
            worker.Path,
            metadataPath,
            app?.Path,
            protocols);
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

    private sealed record RoleProject(string Path, string? Role);
}

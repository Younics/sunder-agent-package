using System.Xml.Linq;
using System.Reflection;
using System.Text.Json;

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

    public string ProgramPath => Path.Combine(DirectoryPath, "obj",
        typeof(RuntimePackageProject).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
        "net10.0", "sunder-analysis", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        "sunder-rpc", "roles", "worker", "worker-entry.g.cs");

    public string? FamilyId => GetProperty("SunderPackageFamilyId");

    public string? Role => GetProperty("SunderPackageRole");

    public string ManifestPath => Path.Combine(DirectoryPath, "bin",
        typeof(RuntimePackageProject).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
        "net10.0", "sunder-dev", "manifest", "sunder-package.json");

    public bool IsWorker => HasTarget("runtime", "worker");

    public bool HasAppRole => HasTarget("app", "avalonia");

    public string? AppAssemblyName => HasAppRole ? Name + ".App" : null;

    public IReadOnlyList<string> RoleProjectPaths =>
    [
        ProjectPath,
        .. AppProjectPath is null ? [] : new[] { AppProjectPath },
        .. ProtocolProjectPaths,
    ];

    private string? GetProperty(string name)
        => GetProjectProperty(ProjectPath, name);

    private bool HasTarget(string role, string kind)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        return manifest.RootElement.GetProperty("targets").EnumerateArray().Any(target =>
            target.GetProperty("role").GetString() == role && target.GetProperty("kind").GetString() == kind);
    }

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
        var projectPath = Path.GetFullPath(Directory.EnumerateFiles(packageDirectory, "*.csproj", SearchOption.TopDirectoryOnly).Single());
        var project = XDocument.Load(projectPath);
        var name = project.Descendants("AssemblyName")
            .Select(static property => property.Value)
            .SingleOrDefault()
            ?? Path.GetFileNameWithoutExtension(projectPath);
        return new RuntimePackageProject(
            name,
            projectPath,
            metadataPath,
            AppProjectPath: null,
            ProtocolProjectPaths: []);
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

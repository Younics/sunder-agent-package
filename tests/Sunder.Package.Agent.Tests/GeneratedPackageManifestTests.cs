using System.Text.Json;
using System.Runtime.InteropServices;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class GeneratedPackageManifestTests
{
    [Fact]
    public void RuntimePackages_GenerateExpectedSdkCompatibilityMetadata()
    {
        var artifactsRoot = ResolveArtifactsRoot();
        var configuration = artifactsRoot is null
            ? ResolveConfiguration()
            : new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        var targetFramework = artifactsRoot is null ? ResolveTargetFramework() : null;
        var runtimePackages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        using var inventoryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "packages.json")));
        var expectedCapabilities = inventoryDocument.RootElement.GetProperty("capabilities");

        Assert.NotEmpty(runtimePackages);
        Assert.Equal(runtimePackages.Count, expectedCapabilities.EnumerateObject().Count());

        foreach (var package in runtimePackages)
        {
            var manifestPath = artifactsRoot is null
                ? package.IsWorker
                    ? Path.Combine(
                        package.DirectoryPath,
                        "obj",
                        configuration,
                        targetFramework!,
                        RuntimeInformation.RuntimeIdentifier,
                        "sunder-package.json")
                    : Path.Combine(package.DirectoryPath, "obj", configuration, targetFramework!, "sunder-package.json")
                : Path.Combine(artifactsRoot, "obj", package.Name, configuration, "sunder-package.json");

            Assert.True(File.Exists(manifestPath), $"Generated manifest was not found for {package.Name}: {manifestPath}");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("archiveFormatVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
            var packageId = root.GetProperty("id").GetString()!;
            var expected = expectedCapabilities.GetProperty(packageId)
                .EnumerateArray()
                .Select(static capability => capability.GetString()!)
                .ToArray();
            var targets = root.GetProperty("targets").EnumerateArray().ToArray();
            Assert.NotEmpty(targets);
            if (package.IsWorker)
            {
                var target = Assert.Single(targets);
                Assert.Equal("worker", target.GetProperty("kind").GetString());
                Assert.Equal(RuntimeInformation.RuntimeIdentifier, target.GetProperty("rid").GetString());
            }
            foreach (var target in targets)
            {
                Assert.StartsWith("1.1.", target.GetProperty("sdkVersion").GetString(), StringComparison.Ordinal);
                var capabilities = target.GetProperty("requiredHostCapabilities")
                    .EnumerateArray()
                    .Select(static capability => capability.GetString()!)
                    .ToArray();
                Assert.Equal(expected, capabilities);
            }

            var packageOutputPath = artifactsRoot is null
                ? package.IsWorker
                    ? Path.Combine(
                        package.DirectoryPath,
                        "bin",
                        configuration,
                        targetFramework!,
                        RuntimeInformation.RuntimeIdentifier)
                    : Path.Combine(package.DirectoryPath, "bin", configuration, targetFramework!)
                : Path.Combine(artifactsRoot, "bin", package.Name, configuration);
            var avaloniaSdkPath = Path.Combine(
                packageOutputPath,
                "sunder-dev",
                "payload",
                "shared",
                "lib",
                "Sunder.Sdk.Avalonia.dll");
            Assert.False(File.Exists(avaloniaSdkPath), $"Host-shared Avalonia SDK assembly was emitted for {package.Name}: {avaloniaSdkPath}");

            if (expected.Contains(SunderSdkCapabilities.StacksRpcV1, StringComparer.Ordinal))
            {
                var stackSdkPath = Path.Combine(
                    packageOutputPath,
                    "sunder-dev",
                    "payload",
                    "shared",
                    "lib",
                    "Sunder.Sdk.Stacks.dll");
                Assert.False(File.Exists(stackSdkPath), $"Host-shared Stack SDK assembly was emitted for {package.Name}: {stackSdkPath}");
            }
        }
    }

    internal static IReadOnlySet<string> CoveredProjectPaths
        => AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Select(static package => package.ProjectPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string ResolveTargetFramework()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";

    private static string? ResolveArtifactsRoot()
    {
        var outputDirectory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var projectOutputDirectory = outputDirectory.Parent;
        var binDirectory = projectOutputDirectory?.Parent;
        return string.Equals(
                   projectOutputDirectory?.Name,
                   typeof(GeneratedPackageManifestTests).Assembly.GetName().Name,
                   StringComparison.Ordinal)
               && string.Equals(binDirectory?.Name, "bin", StringComparison.OrdinalIgnoreCase)
            ? binDirectory?.Parent?.FullName
            : null;
    }
}

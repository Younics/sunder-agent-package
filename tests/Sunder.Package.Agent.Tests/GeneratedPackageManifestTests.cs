using System.Text.Json;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class GeneratedPackageManifestTests
{
    [Fact]
    public void RuntimePackages_GenerateExpectedSdkCompatibilityMetadata()
    {
        var configuration = ResolveConfiguration();
        var targetFramework = ResolveTargetFramework();
        var runtimePackages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        using var inventoryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "packages.json")));
        var expectedCapabilities = inventoryDocument.RootElement.GetProperty("capabilities");

        Assert.NotEmpty(runtimePackages);
        Assert.Equal(runtimePackages.Count, expectedCapabilities.EnumerateObject().Count());

        foreach (var package in runtimePackages)
        {
            var manifestPath = Path.Combine(
                package.DirectoryPath,
                "obj",
                configuration,
                targetFramework,
                "sunder-package.json");

            Assert.True(File.Exists(manifestPath), $"Generated manifest was not found for {package.Name}: {manifestPath}");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("sdkApiVersion").GetInt32());
            Assert.StartsWith("1.1.", root.GetProperty("sdkPackageVersion").GetString(), StringComparison.Ordinal);
            var capabilities = root.GetProperty("requiredSdkCapabilities")
                .EnumerateArray()
                .Select(static capability => capability.GetString()!)
                .ToArray();
            var packageId = root.GetProperty("id").GetString()!;
            var expected = expectedCapabilities.GetProperty(packageId)
                .EnumerateArray()
                .Select(static capability => capability.GetString()!)
                .ToArray();
            Assert.Equal(expected, capabilities);

            var avaloniaSdkPath = Path.Combine(
                package.DirectoryPath,
                "bin",
                configuration,
                targetFramework,
                "sunder-dev",
                "lib",
                "Sunder.Sdk.Avalonia.dll");
            Assert.False(File.Exists(avaloniaSdkPath), $"Host-shared Avalonia SDK assembly was emitted for {package.Name}: {avaloniaSdkPath}");

            if (capabilities.Contains(SunderSdkCapabilities.StackContributionsV1, StringComparer.Ordinal))
            {
                var stackSdkPath = Path.Combine(
                    package.DirectoryPath,
                    "bin",
                    configuration,
                    targetFramework,
                    "sunder-dev",
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
}

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

        Assert.NotEmpty(runtimePackages);
        Assert.Empty(ExpectedPackageCapabilities.Keys.Except(
            runtimePackages.Select(static package => package.Name),
            StringComparer.OrdinalIgnoreCase));

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
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("sdkPackageVersion").GetString()));
            var capabilities = root.GetProperty("requiredSdkCapabilities")
                .EnumerateArray()
                .Select(static capability => capability.GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains(SunderSdkCapabilities.CoreV1, capabilities);
            Assert.Contains(SunderSdkCapabilities.PackagingV1, capabilities);
            Assert.Contains(SunderSdkCapabilities.ContributionsV1, capabilities);
            foreach (var expectedCapability in ExpectedPackageCapabilities.GetValueOrDefault(package.Name, []))
            {
                Assert.Contains(expectedCapability, capabilities);
            }

            var avaloniaSdkPath = Path.Combine(
                package.DirectoryPath,
                "bin",
                configuration,
                targetFramework,
                "sunder-dev",
                "lib",
                "Sunder.Sdk.Avalonia.dll");
            Assert.False(File.Exists(avaloniaSdkPath), $"Host-shared Avalonia SDK assembly was emitted for {package.Name}: {avaloniaSdkPath}");

            if (capabilities.Contains(SunderSdkCapabilities.StackContributionsV1))
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

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedPackageCapabilities = new Dictionary<string, string[]>
    {
        ["Sunder.Package.Agent"] =
        [
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.ViewsV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.ExtensionChangesV1,
            SunderSdkCapabilities.LoggingV1,
            SunderSdkCapabilities.NotificationsV1,
            SunderSdkCapabilities.ShellViewV1,
            SunderSdkCapabilities.StorageV1,
            SunderSdkCapabilities.LocalWorkspaceV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StackContributionsV1,
        ],
        ["Sunder.Package.Agent.Builder"] = [SunderSdkCapabilities.ViewsV1],
        ["Sunder.Package.Agent.Tools.Web"] = [SunderSdkCapabilities.ConfigurationSchemaV1, SunderSdkCapabilities.ExtensionsV1],
        ["Sunder.Package.Agent.Tools.Shell"] = [SunderSdkCapabilities.ExtensionsV1],
        ["Sunder.Package.Agent.Tools.Files"] = [SunderSdkCapabilities.ExtensionsV1],
        ["Sunder.Package.Agent.Subagents"] =
        [
            SunderSdkCapabilities.ViewsV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StackContributionsV1,
            SunderSdkCapabilities.LocalWorkspaceV1,
        ],
        ["Sunder.Package.Agent.Skills"] =
        [
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StackContributionsV1,
            SunderSdkCapabilities.LocalWorkspaceV1,
        ],
        ["Sunder.Package.Agent.Provider.OpenAI"] =
        [
            SunderSdkCapabilities.ConfigurationSchemaV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.AuthV1,
            SunderSdkCapabilities.CallbacksV1,
            SunderSdkCapabilities.ConfigurationValuesV1,
            SunderSdkCapabilities.SecretsV1,
        ],
        ["Sunder.Package.Agent.Provider.LMStudio"] = [SunderSdkCapabilities.ConfigurationSchemaV1, SunderSdkCapabilities.ExtensionsV1],
        ["Sunder.Package.Agent.Provider.Gemini"] = [SunderSdkCapabilities.ConfigurationSchemaV1, SunderSdkCapabilities.ExtensionsV1],
        ["Sunder.Package.Agent.Provider.Anthropic"] = [SunderSdkCapabilities.ConfigurationSchemaV1, SunderSdkCapabilities.ExtensionsV1],
        ["Sunder.Package.Agent.Memory.Semantic"] =
        [
            SunderSdkCapabilities.ConfigurationSchemaV1,
            SunderSdkCapabilities.BackgroundServicesV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.ViewsV1,
            SunderSdkCapabilities.LocalWorkspaceV1,
        ],
        ["Sunder.Package.Agent.Mcp"] =
        [
            SunderSdkCapabilities.ConfigurationSchemaV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StackContributionsV1,
        ],
        ["Sunder.Package.Agent.Execution.Local"] =
        [
            SunderSdkCapabilities.ConfigurationSchemaV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.ExtensionsV1,
        ],
        ["Sunder.Package.Agent.Execution.Docker"] =
        [
            SunderSdkCapabilities.ConfigurationSchemaV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.BackgroundProcessesV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StackContributionsV1,
            SunderSdkCapabilities.LocalWorkspaceV1,
        ],
    };

    private static string ResolveTargetFramework()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";
}

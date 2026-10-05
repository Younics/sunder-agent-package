using System.Text.Json;
using System.Runtime.InteropServices;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class GeneratedPackageManifestTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerPackages_GenerateExpectedSdkCompatibilityMetadata()
    {
        var artifactsRoot = ResolveArtifactsRoot();
        var configuration = artifactsRoot is null
            ? ResolveConfiguration()
            : new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        var targetFramework = artifactsRoot is null ? ResolveTargetFramework() : null;
        var runtimePackages = AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Where(static package => package.IsWorker)
            .ToArray();
        using var inventoryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "packages.json")));
        var expectedCapabilities = inventoryDocument.RootElement.GetProperty("capabilities");
        var releaseVersion = inventoryDocument.RootElement.GetProperty("releaseVersion").GetString();
        var releaseTitles = inventoryDocument.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Where(static package => package.GetProperty("artifactType").GetString() == "sunderpkg")
            .ToDictionary(
                static package => package.GetProperty("packageId").GetString()!,
                static package => package.GetProperty("releaseTitle").GetString()!,
                StringComparer.Ordinal);

        Assert.NotEmpty(runtimePackages);

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
            Assert.Equal(package.FamilyId, packageId);
            Assert.Equal(releaseVersion, root.GetProperty("version").GetString());
            Assert.Equal(releaseTitles[packageId], root.GetProperty("name").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("summary").GetString()));
            Assert.Equal("assets/icon.png", root.GetProperty("icon").GetString());
            if (packageId is "sunder.package.agent" or "sunder.package.agent.mcp")
            {
                var observedBundle = Assert.Single(
                    root.GetProperty("contractBundles").EnumerateArray(),
                    static bundle => bundle.GetProperty("contractId").GetString()
                        == "sunder.agent.tool.execution.observed");
                Assert.Equal("1.0.0", observedBundle.GetProperty("version").GetString());
                Assert.Equal(
                    "contracts/tool-execution-observed.rpc.json",
                    observedBundle.GetProperty("descriptorPath").GetString());
                Assert.Equal(
                    "1e80a60bbba1d0bbfdcd20992b3ad6e6b6d9166fc987056d6945316a611bbee2",
                    observedBundle.GetProperty("descriptorSha256").GetString());
                Assert.Equal(
                    "1a34bc8a004d854ae180dd9ac5a9fb4c346de4bd3360c0ec53821d74e361657d",
                    observedBundle.GetProperty("abiSha256").GetString());
                Assert.Equal("public", observedBundle.GetProperty("exposure").GetString());
            }
            if (packageId == "sunder.package.agent")
            {
                var observedUse = Assert.Single(
                    root.GetProperty("usesContracts").EnumerateArray(),
                    static use => use.GetProperty("contractId").GetString()
                        == "sunder.agent.tool.execution.observed");
                Assert.False(observedUse.GetProperty("required").GetBoolean());
                Assert.Equal(
                    ["discover", "invoke", "subscribe"],
                    observedUse.GetProperty("actions").EnumerateArray()
                        .Select(static action => action.GetString()!)
                        .ToArray());
            }
            if (packageId == "sunder.package.agent.mcp")
            {
                var observedProvider = Assert.Single(
                    root.GetProperty("provides").EnumerateArray(),
                    static provider => provider.GetProperty("providerId").GetString() == "mcp.tools.observed");
                Assert.Equal(
                    "sunder.agent.tool.execution.observed",
                    observedProvider.GetProperty("contractId").GetString());
                Assert.Equal("1.0.0", observedProvider.GetProperty("contractVersion").GetString());
                Assert.Equal("runtime", observedProvider.GetProperty("role").GetString());
                Assert.Equal("public", observedProvider.GetProperty("audience").GetString());
            }
            var dependencies = root.GetProperty("dependsOn").EnumerateArray().ToArray();
            if (packageId == "sunder.package.agent")
            {
                Assert.Empty(dependencies);
            }
            else
            {
                var baseAgent = Assert.Single(dependencies);
                Assert.Equal("sunder.package.agent", baseAgent.GetProperty("packageId").GetString());
                Assert.Equal(">=2.0.0 <3.0.0", baseAgent.GetProperty("versionRange").GetString());
            }
            var expected = expectedCapabilities.GetProperty(packageId)
                .EnumerateArray()
                .Select(static capability => capability.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var targets = root.GetProperty("targets").EnumerateArray().ToArray();
            Assert.NotEmpty(targets);
            var workerTarget = Assert.Single(targets);
            Assert.Equal("worker", workerTarget.GetProperty("kind").GetString());
            Assert.Equal(RuntimeInformation.RuntimeIdentifier, workerTarget.GetProperty("rid").GetString());
            Assert.Equal($"lib/{package.Name}.dll", workerTarget.GetProperty("entryPoint").GetString());
            var executionRuntime = workerTarget.GetProperty("executionRuntime");
            Assert.Equal("sunder.runtime.dotnet", executionRuntime.GetProperty("id").GetString());
            Assert.Equal(">=10.0.0 <11.0.0", executionRuntime.GetProperty("versionRange").GetString());
            Assert.Equal("sunder.dotnet-worker-module.v1", executionRuntime.GetProperty("moduleAbi").GetString());
            foreach (var target in targets)
            {
                Assert.StartsWith("1.1.", target.GetProperty("sdkVersion").GetString(), StringComparison.Ordinal);
                var capabilities = target.GetProperty("requiredHostCapabilities")
                    .EnumerateArray()
                    .Select(static capability => capability.GetString()!)
                    .ToArray();
                Assert.All(capabilities, capability => Assert.Contains(capability, expected));
                Assert.Contains(SunderSdkCapabilities.RpcV1, capabilities, StringComparer.Ordinal);
                Assert.Contains("core.v1", capabilities, StringComparer.Ordinal);
                Assert.Contains("packaging.v1", capabilities, StringComparer.Ordinal);
                Assert.Contains("sdk-baseline-1-1.v1", capabilities, StringComparer.Ordinal);
                Assert.Contains("worker-protocol.v1", capabilities, StringComparer.Ordinal);
                Assert.DoesNotContain("runtime-operations.v1", capabilities, StringComparer.Ordinal);
                Assert.DoesNotContain("runtime-invocation-errors.v1", capabilities, StringComparer.Ordinal);
                Assert.DoesNotContain(SunderSdkCapabilities.ThemingV1, capabilities, StringComparer.Ordinal);
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
            var familyOutputPath = artifactsRoot is null
                ? Path.Combine(package.DirectoryPath, "bin", configuration, targetFramework!)
                : packageOutputPath;
            using (var canonicalManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                       familyOutputPath,
                       "sunder-dev",
                       "manifest",
                       "sunder-package.json"))))
            {
                var canonicalCapabilities = canonicalManifest.RootElement.GetProperty("targets")
                    .EnumerateArray()
                    .SelectMany(static target => target.GetProperty("requiredHostCapabilities").EnumerateArray()
                        .Select(static capability => capability.GetString()!))
                    .ToHashSet(StringComparer.Ordinal);
                Assert.True(
                    expected.SetEquals(canonicalCapabilities),
                    $"{packageId} canonical capabilities differ. Missing: "
                    + $"{string.Join(", ", expected.Except(canonicalCapabilities).Order(StringComparer.Ordinal))}. "
                    + $"Unexpected: {string.Join(", ", canonicalCapabilities.Except(expected).Order(StringComparer.Ordinal))}.");
            }
            var avaloniaSdkPath = Path.Combine(
                packageOutputPath,
                "sunder-dev",
                "payload",
                "shared",
                "lib",
                "Sunder.Sdk.Avalonia.dll");
            Assert.False(File.Exists(avaloniaSdkPath), $"Host-shared Avalonia SDK assembly was emitted for {package.Name}: {avaloniaSdkPath}");

            if (package.IsWorker)
            {
                var payloadRoot = Path.Combine(
                    packageOutputPath,
                    "sunder-dev",
                    "payload");
                var workerRoot = Path.Combine(
                    payloadRoot,
                    "runtime",
                    RuntimeInformation.RuntimeIdentifier);
                Assert.True(Directory.Exists(workerRoot), $"Worker payload was not found for {package.Name}: {workerRoot}");
                var forbiddenWorkerFiles = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .Where(static fileName => fileName is not null &&
                        (fileName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                         || fileName.StartsWith("CommunityToolkit", StringComparison.OrdinalIgnoreCase)
                         || fileName.StartsWith("LiveMarkdown", StringComparison.OrdinalIgnoreCase)
                         || fileName.Contains("AvaloniaEdit", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                Assert.Empty(forbiddenWorkerFiles);

                var entryPoint = Assert.Single(targets).GetProperty("entryPoint").GetString()!;
                var modulePath = Path.Combine(
                    workerRoot,
                    entryPoint.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(modulePath), $"Worker managed module was not found for {package.Name}: {modulePath}");
                Assert.Equal(".dll", Path.GetExtension(modulePath));
                var moduleBase = modulePath[..^".dll".Length];
                Assert.False(File.Exists(moduleBase), $"Obsolete Worker apphost was emitted for {package.Name}: {moduleBase}");
                Assert.False(File.Exists(moduleBase + ".exe"), $"Obsolete Worker apphost was emitted for {package.Name}: {moduleBase}.exe");
                var runtimeConfigPath = moduleBase + ".runtimeconfig.json";
                var dependencyManifestPath = moduleBase + ".deps.json";
                Assert.True(File.Exists(runtimeConfigPath), $"Worker runtimeconfig was not found for {package.Name}: {runtimeConfigPath}");
                Assert.True(File.Exists(dependencyManifestPath), $"Worker dependency manifest was not found for {package.Name}: {dependencyManifestPath}");
                using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
                var runtimeOptions = runtimeConfig.RootElement.GetProperty("runtimeOptions");
                Assert.Equal("net10.0", runtimeOptions.GetProperty("tfm").GetString());
                Assert.False(runtimeOptions.TryGetProperty("includedFrameworks", out _));
                var framework = runtimeOptions.GetProperty("framework");
                Assert.Equal("Microsoft.NETCore.App", framework.GetProperty("name").GetString());
                Assert.StartsWith("10.", framework.GetProperty("version").GetString(), StringComparison.Ordinal);
                using var dependencyManifest = JsonDocument.Parse(File.ReadAllText(dependencyManifestPath));
                Assert.Equal(
                    $".NETCoreApp,Version=v10.0/{RuntimeInformation.RuntimeIdentifier}",
                    dependencyManifest.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString());
            }

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
            .Where(static package => package.IsWorker)
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

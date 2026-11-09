using System.Reflection;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Each tool suite supplies its own reviewed contribution inventory.
public sealed partial class ToolPackageRoleCompositionTests
{
    [Fact]
    public void ToolPackage_ExposesOnlyWorkerRole()
    {
        var assembly = Assembly.Load(PackageAssemblyName);
        Assert.DoesNotContain(assembly.GetTypes().Where(static type => !type.IsAbstract),
            typeof(ISunderAppPackageModule).IsAssignableFrom);
        Assert.NotNull(assembly.EntryPoint);
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerRole_GeneratesExactContributions()
    {
        var package = Assert.Single(AgentPackageRepositoryInventory.GetRuntimePackageProjects(),
            package => package.Name == PackageAssemblyName);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(package.ManifestPath));
        var actual = manifest.RootElement.GetProperty("provides").EnumerateArray()
            .Select(static provider => "rpc-provider:" + provider.GetProperty("providerId").GetString()).ToList();
        if (manifest.RootElement.GetProperty("targets")[0].GetProperty("requiredHostCapabilities")
            .EnumerateArray().Any(static capability => capability.GetString() == "settings.schema.v1"))
        {
            actual.Add("settings-schema");
        }
        Assert.Equal(ExpectedRegistrations.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }
}

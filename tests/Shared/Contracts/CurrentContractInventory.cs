using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Authored expectations, independent of receipts, owned files, and generated output.
internal static class CurrentContractInventory
{
    internal const string RelativePath = "compatibility/agent-current-inventory.json";
    internal const string AgentId = "younics.ai.agent";
    private static readonly Inventory Data = Read();
    internal static IReadOnlyList<Package> Packages => Data.Packages;
    internal static IReadOnlyList<Contract> Contracts => Data.Contracts;
    internal static string PackageVersion => Data.PackageVersion;
    internal static string PublicVersion => Data.PublicVersion;
    internal static Contract Find(string id) => Assert.Single(Contracts, value => value.ContractId == id);
    internal static Package Owner(string id) => Assert.Single(Packages, value => value.PackageId == id);

    internal static void AssertDescriptor(SunderRpcContractDescriptor descriptor)
    {
        var expected = Find(descriptor.ContractId);
        Assert.Equal(expected.Version, descriptor.Version);
        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, descriptor.SchemaProfile);
        var service = Assert.Single(descriptor.Services);
        Assert.Equal(expected.Service, service.ServiceId);
        Assert.Equal(expected.Methods.Order(StringComparer.Ordinal), service.Methods.Select(value => value.MethodId).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Streams.Order(StringComparer.Ordinal), service.Methods
            .Where(value => value.Kind == SunderRpcMethodKind.ServerStream).Select(value => value.MethodId).Order(StringComparer.Ordinal));
    }

    internal static void AssertPackageInventory(JsonElement inventory)
    {
        Assert.Equal(PackageVersion, inventory.GetProperty("releaseVersion").GetString());
        var packages = inventory.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(Packages.Select(value => value.PackageId).Order(StringComparer.Ordinal),
            packages.Select(value => value.GetProperty("packageId").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(Packages.Select(value => value.PackageId).Order(StringComparer.Ordinal),
            inventory.GetProperty("capabilities").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        foreach (var expected in Packages)
        {
            var actual = Assert.Single(packages, value => value.GetProperty("packageId").GetString() == expected.PackageId);
            Assert.Equal(expected.Key, actual.GetProperty("key").GetString());
            Assert.Equal(expected.ProjectPath, actual.GetProperty("projectPath").GetString());
            Assert.Equal("sunderpkg", actual.GetProperty("artifactType").GetString());
            Assert.Equal(expected.Providers.Keys.Order(StringComparer.Ordinal),
                actual.GetProperty("providerIds").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        }
    }

    internal static IReadOnlyList<Use> Uses(Package package)
    {
        var result = Contracts.Where(value => value.PackageId == package.PackageId && value.App)
            .Select(value => new Use(value.ContractId, "app", value.Exposure == "package", value.Actions)).ToList();
        foreach (var contract in Contracts.Where(value => value.Exposure == "public" &&
                     (package.PackageApi || package.PackageId == AgentId && value.Runtime)))
            result.Add(new(contract.ContractId, "runtime", package.RequiredRuntime.Contains(contract.ContractId, StringComparer.Ordinal), contract.Actions));
        if (package.Key == "agent-tools-web")
            result.Add(new("younics.ai.agent.execution.target", "runtime", false, ["discover"]));
        return result;
    }

    private static Inventory Read()
    {
        var value = JsonSerializer.Deserialize<Inventory>(File.ReadAllBytes(Path.Combine(
            RegressionTestRpcDescriptors.RepositoryRoot, RelativePath)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, value.SchemaVersion);
        Assert.Equal(15, value.Packages.Length);
        Assert.Equal(12, value.Packages.Count(package => package.HasApp));
        Assert.Equal(18, value.Contracts.Count(contract => contract.Exposure == "public"));
        Assert.Equal(13, value.Contracts.Count(contract => contract.Exposure == "package"));
        Assert.Equal(31, value.Contracts.Select(contract => contract.ContractId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(15, value.Packages.Select(package => package.ProjectPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(15, value.Packages.Select(package => package.PackageId).Distinct(StringComparer.Ordinal).Count());
        foreach (var contract in value.Contracts)
        {
            Assert.Contains(value.Packages, package => package.PackageId == contract.PackageId
                && contract.Source.StartsWith(package.SourceDirectory + "/", StringComparison.Ordinal));
            Assert.NotEmpty(contract.Methods);
            Assert.Equal(contract.Methods.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), contract.Methods);
            Assert.All(contract.Streams, method => Assert.Contains(method, contract.Methods));
        }
        return value;
    }

    internal sealed record Package(string Key, string PackageId, string ProjectPath, bool HasApp,
        bool PackageApi, string[] RequiredRuntime, Dictionary<string, string> Providers)
    {
        internal string SourceDirectory => Path.GetDirectoryName(ProjectPath)!.Replace('\\', '/');
        internal string Name => Path.GetFileNameWithoutExtension(ProjectPath);
    }

    internal sealed record Contract(string ContractId, string PackageId, string Exposure, string Source,
        string Service, string[] Methods, string[] Streams, bool Runtime, bool App, string? ContractVersion = null)
    {
        internal string Version => ContractVersion ?? (Exposure == "public" ? PublicVersion : PackageVersion);
        internal string[] Actions => Streams.Length == 0 ? ["discover", "invoke"] : ["discover", "invoke", "subscribe"];
    }

    internal sealed record Use(string ContractId, string Role, bool Required, string[] Actions);
    private sealed record Inventory(int SchemaVersion, string PackageVersion, string PublicVersion, Package[] Packages, Contract[] Contracts);
}

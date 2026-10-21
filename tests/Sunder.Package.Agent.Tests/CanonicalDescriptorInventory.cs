using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class CanonicalDescriptorInventory
{
    internal sealed record Entry(string ProjectDirectory, string Slot, string SourcePath, string Exposure, SunderRpcContractDescriptor Descriptor);

    internal static IReadOnlyList<Entry> Read()
    {
        var result = new List<Entry>();
        foreach (var package in CurrentContractInventory.Packages)
        {
            var directory = Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, package.SourceDirectory);
            var ownedPath = Path.Combine(directory, "sunder-contracts.owned.json");
            using var owned = File.Exists(ownedPath) ? JsonDocument.Parse(File.ReadAllBytes(ownedPath)) : null;
            JsonProperty[] publicEntries = [];
            if (owned is not null)
            {
                Assert.Equal(1, owned.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("package", owned.RootElement.GetProperty("authorityKind").GetString());
                Assert.Equal(package.PackageId, owned.RootElement.GetProperty("authorityId").GetString());
                Assert.Equal(package.PackageId, owned.RootElement.GetProperty("packageId").GetString());
                publicEntries = owned.RootElement.GetProperty("contracts").EnumerateObject().ToArray();
            }
            Assert.Equal(CurrentContractInventory.Contracts.Where(value => value.PackageId == package.PackageId && value.Exposure == "public")
                    .Select(value => value.ContractId).Order(StringComparer.Ordinal),
                publicEntries.Select(value => value.Value.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalPackageRoles.Analysis(directory), "authoring-receipt.v1.json")));
            Assert.Equal(1, receipt.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(package.PackageId, receipt.RootElement.GetProperty("packageId").GetString());
            Assert.Equal(CurrentContractInventory.PackageVersion, receipt.RootElement.GetProperty("packageVersion").GetString());
            var bundles = receipt.RootElement.GetProperty("bundles").EnumerateArray().ToDictionary(value => value.GetProperty("contractKey").GetString()!);
            foreach (var contract in receipt.RootElement.GetProperty("contracts").EnumerateArray())
            {
                var slot = contract.GetProperty("contractKey").GetString()!;
                var bundle = bundles[slot];
                if (bundle.GetProperty("source").GetString() != "owned") continue;
                Assert.Equal("package", bundle.GetProperty("authorityKind").GetString());
                Assert.Equal(package.PackageId, bundle.GetProperty("authorityId").GetString());
                var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
                CurrentContractRecords.AssertCurrent(descriptor);
                Assert.Equal(descriptor.Version, contract.GetProperty("contractVersion").GetString());
                Assert.Equal(descriptor.GetCanonicalUtf8().Length, contract.GetProperty("descriptorBytes").GetInt32());
                Assert.Equal(descriptor.Sha256, contract.GetProperty("descriptorSha256").GetString());
                Assert.Equal(descriptor.AbiSha256, contract.GetProperty("abiSha256").GetString());
                var expected = CurrentContractInventory.Find(descriptor.ContractId);
                Assert.Equal(package.PackageId, expected.PackageId);
                Assert.Equal(expected.Exposure, contract.GetProperty("exposure").GetString());
                if (expected.Exposure == "public")
                {
                    var current = Assert.Single(publicEntries, value => value.Value.GetProperty("contractId").GetString() == descriptor.ContractId).Value.GetProperty("current");
                    Assert.Equal("release-frozen", current.GetProperty("state").GetString());
                    Assert.Equal("public", current.GetProperty("exposure").GetString());
                    Assert.Equal(descriptor.Version, current.GetProperty("contractVersion").GetString());
                    Assert.Equal(descriptor.GetCanonicalUtf8().Length, current.GetProperty("descriptorBytes").GetInt32());
                    Assert.Equal(descriptor.GetCanonicalAbiProjectionUtf8().Length, current.GetProperty("abiProjectionBytes").GetInt32());
                    Assert.Equal(package.PackageId, current.GetProperty("packageRelease").GetProperty("packageId").GetString());
                    Assert.Equal(CurrentContractInventory.PackageVersion, current.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
                    Assert.Equal(descriptor.GetCanonicalUtf8(), Encoding.UTF8.GetBytes(current.GetProperty("canonicalDescriptor").GetString()!));
                    Assert.Equal(descriptor.Sha256, current.GetProperty("descriptorSha256").GetString());
                    Assert.Equal(descriptor.AbiSha256, current.GetProperty("abiSha256").GetString());
                    Assert.Equal(descriptor.GetCanonicalAbiProjectionUtf8(), Encoding.UTF8.GetBytes(current.GetProperty("canonicalAbiProjection").GetString()!));
                }
                result.Add(new(directory, slot, expected.Source, expected.Exposure, descriptor));
            }
        }
        Assert.Equal(CurrentContractInventory.Contracts.Select(value => value.ContractId).Order(StringComparer.Ordinal),
            result.Select(value => value.Descriptor.ContractId).Order(StringComparer.Ordinal));
        return result;
    }

    internal static IReadOnlyList<Entry> PublicAgent() => Read().Where(value => value.Exposure == "public").ToArray();
    internal static JsonDocument Document(string id) => JsonDocument.Parse(CurrentContractRecords.Get(id).GetCanonicalUtf8());
}

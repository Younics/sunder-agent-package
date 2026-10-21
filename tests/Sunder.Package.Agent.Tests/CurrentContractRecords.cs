using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Separately reviewed byte records. The verifier can propose records from qualified
// current artifacts; neither tests nor the verifier update this expected input.
internal static class CurrentContractRecords
{
    internal const string RelativePath = "compatibility/agent-current-contracts.json";
    private static readonly Lazy<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> Records = new(Read);

    internal static SunderRpcContractDescriptor Get(string id)
        => Records.Value.TryGetValue(id, out var value) ? value
            : throw new InvalidOperationException($"No reviewed current contract '{id}'.");

    internal static void AssertCurrent(SunderRpcContractDescriptor actual)
    {
        CurrentContractInventory.AssertDescriptor(actual);
        var expected = Get(actual.ContractId);
        Assert.Equal(expected.GetCanonicalUtf8(), actual.GetCanonicalUtf8());
        Assert.Equal(expected.GetCanonicalAbiProjectionUtf8(), actual.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.AbiSha256, actual.AbiSha256);
    }

    private static IReadOnlyDictionary<string, SunderRpcContractDescriptor> Read()
    {
        var root = RegressionTestRpcDescriptors.RepositoryRoot;
        var path = Path.Combine(root, RelativePath);
        Assert.True(File.Exists(path), $"Missing reviewed current contract records: {RelativePath}. Complete owning source/Accept/Freeze/selection and current artifact qualification, then review the current-contracts verifier proposal. Old captures are not substitutes.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, CurrentContractInventory.RelativePath)))),
            document.RootElement.GetProperty("inventorySha256").GetString());
        var entries = document.RootElement.GetProperty("contracts").EnumerateArray().ToArray();
        Assert.Equal(CurrentContractInventory.Contracts.Select(value => value.ContractId).Order(StringComparer.Ordinal),
            entries.Select(value => value.GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
        return entries.ToDictionary(entry => entry.GetProperty("contractId").GetString()!, entry =>
        {
            var expected = CurrentContractInventory.Find(entry.GetProperty("contractId").GetString()!);
            Assert.Equal(expected.PackageId, entry.GetProperty("packageId").GetString());
            Assert.Equal(expected.Exposure, entry.GetProperty("exposure").GetString());
            var bytes = Encoding.UTF8.GetBytes(entry.GetProperty("canonicalDescriptor").GetString()!);
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            Assert.Equal(expected.ContractId, descriptor.ContractId);
            CurrentContractInventory.AssertDescriptor(descriptor);
            Assert.Equal(bytes, descriptor.GetCanonicalUtf8());
            Assert.Equal(entry.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
            Assert.Equal(entry.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
            return descriptor;
        }, StringComparer.Ordinal);
    }
}

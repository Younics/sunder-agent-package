using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class CanonicalDescriptorInventory
{
    internal sealed record Entry(string ProjectDirectory, string Slot, string HistoricalPath, string Exposure, SunderRpcContractDescriptor Descriptor);

    internal static IReadOnlyList<Entry> Read()
    {
        using var frozen = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot,
            "compatibility/agent-rpc-migration-baseline.json")));
        var original = frozen.RootElement.GetProperty("descriptors").EnumerateArray()
            .ToDictionary(value => value.GetProperty("contractId").GetString()!, StringComparer.Ordinal);
        var result = new List<Entry>();
        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects())
        {
            using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package.DirectoryPath, "sunder-contracts.owned.json")));
            foreach (var slot in owned.RootElement.GetProperty("contracts").EnumerateObject())
            {
                var current = slot.Value.GetProperty("current");
                var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(current.GetProperty("canonicalDescriptor").GetString()!));
                var oracle = CurrentPublicRpcDescriptors.Get(descriptor.ContractId);
                Assert.Equal(oracle.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
                Assert.Equal(oracle.AbiSha256, descriptor.AbiSha256);
                Assert.Equal("release-frozen", current.GetProperty("state").GetString());
                var history = original[descriptor.ContractId];
                result.Add(new(package.DirectoryPath, slot.Name, history.GetProperty("path").GetString()!,
                    history.GetProperty("exposure").GetString()!, descriptor));
            }
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                CanonicalPackageRoles.Analysis(package.DirectoryPath), "authoring-receipt.v1.json")));
            foreach (var contract in receipt.RootElement.GetProperty("contracts").EnumerateArray()
                         .Where(value => value.GetProperty("exposure").GetString() == "package"))
            {
                var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
                CurrentPrivateRpcDescriptors.AssertCurrent(descriptor);
                var historyPath = original.TryGetValue(descriptor.ContractId, out var history)
                    ? history.GetProperty("path").GetString()!
                    : "src/Sunder.Package.Agent/Services/AgentProfileService.cs";
                result.Add(new(package.DirectoryPath, contract.GetProperty("contractKey").GetString()!, historyPath, "package", descriptor));
            }
        }
        Assert.Equal(32, result.Count);
        Assert.Equal(original.Keys.Append("sunder.package.agent.agent.profile").Order(), result.Select(value => value.Descriptor.ContractId).Order());
        return result;
    }

    internal static IReadOnlyList<Entry> PublicAgent() => Read().Where(value => value.Exposure == "public").ToArray();
    internal static JsonDocument Document(string contractId)
        => JsonDocument.Parse(Assert.Single(Read(), value => value.Descriptor.ContractId == contractId).Descriptor.GetCanonicalUtf8());
}

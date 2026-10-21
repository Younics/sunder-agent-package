using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class CurrentPublicRpcDescriptors
{
    internal const string ContractVersion = "1.0.0";
    internal static readonly IReadOnlySet<string> ContractIds = CurrentContractInventory.Contracts
        .Where(value => value.Exposure == "public").Select(value => value.ContractId).ToHashSet(StringComparer.Ordinal);

    internal static bool IsAgentPublicContract(string id) => ContractIds.Contains(id);
    internal static SunderRpcContractDescriptor Get(string id)
    {
        Assert.Contains(id, ContractIds);
        return CurrentContractRecords.Get(id);
    }

    internal static void AssertCurrent(SunderRpcContractDescriptor descriptor)
    {
        Assert.Contains(descriptor.ContractId, ContractIds);
        CurrentContractRecords.AssertCurrent(descriptor);
    }
}

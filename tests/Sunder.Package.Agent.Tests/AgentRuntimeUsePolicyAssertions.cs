using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class AgentRuntimeUsePolicyAssertions
{
    internal static void AssertWorkerReceipt(string repository, JsonElement receipt)
    {
        var expected = CurrentContractInventory.Uses(CurrentContractInventory.Owner(CurrentContractInventory.AgentId))
            .Where(value => value.Role == "runtime").ToArray();
        var contracts = receipt.GetProperty("contracts").EnumerateArray().ToDictionary(value => value.GetProperty("contractKey").GetString()!);
        var uses = receipt.GetProperty("uses").EnumerateArray().ToArray();
        Assert.Equal(15, expected.Length);
        Assert.Equal(expected.Select(value => value.ContractId).Order(StringComparer.Ordinal),
            uses.Select(value => contracts[value.GetProperty("contractKey").GetString()!].GetProperty("contractId").GetString()).Order(StringComparer.Ordinal));
        foreach (var use in uses)
        {
            Assert.Equal("runtime", use.GetProperty("role").GetString());
            var contract = contracts[use.GetProperty("contractKey").GetString()!];
            var policy = Assert.Single(expected, value => value.ContractId == contract.GetProperty("contractId").GetString());
            Assert.Equal(CurrentContractInventory.PublicVersion, contract.GetProperty("contractVersion").GetString());
            Assert.Equal(policy.Required, use.GetProperty("required").GetBoolean());
            Assert.Equal(policy.Actions, use.GetProperty("actions").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(policy.Required, contract.GetProperty("runtimeUse").GetProperty("required").GetBoolean());
            Assert.True(JsonElement.DeepEquals(use.GetProperty("actions"), contract.GetProperty("runtimeUse").GetProperty("actions")));
        }
        Assert.Equal(15, contracts.Values.Count(value => value.TryGetProperty("runtimeUse", out _)));
    }
}

using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static class CurrentPrivateRpcDescriptors
{
    internal const string OwnerId = "younics.ai.agent.owner.runtime";
    internal static SunderRpcContractDescriptor Get(string id) => CurrentContractRecords.Get(id);

    internal static void AssertCurrent(SunderRpcContractDescriptor descriptor)
    {
        Assert.Equal("package", CurrentContractInventory.Find(descriptor.ContractId).Exposure);
        CurrentContractRecords.AssertCurrent(descriptor);
    }

    internal static JsonSerializerOptions OwnerJson => (JsonSerializerOptions)typeof(AgentProfileRecord).Assembly
        .GetType("Sunder.Package.Agent.Runtime.AgentOwnerRuntimeProvider", throwOnError: true)!
        .GetNestedType("__SunderGeneratedRpcHandler", BindingFlags.NonPublic)!
        .GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}

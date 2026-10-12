using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Current private expectations are separate from the immutable 31-contract oracle.
// Reviewed successor tuples are independent of generated output and frozen history.
internal static class CurrentPrivateRpcDescriptors
{
    internal const string OwnerId = "sunder.agent.owner.runtime";
    private static readonly IReadOnlyDictionary<string, JsonElement> Successors = ReadSuccessors();
    private static Assembly OwnerAssembly => typeof(AgentProfileRecord).Assembly;

    internal static SunderRpcContractDescriptor Get(string contractId)
    {
        if (Successors.TryGetValue(contractId, out var expected))
        {
            var path = Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot,
                expected.GetProperty("sourceDirectory").GetString()!, "obj", OwnerAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
                "net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc/authoring-receipt.v1.json");
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(expected.GetProperty("packageId").GetString(), receipt.RootElement.GetProperty("packageId").GetString());
            var contract = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray(), value => value.GetProperty("contractId").GetString() == contractId);
            var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
            AssertCurrent(descriptor);
            return descriptor;
        }
        return CurrentPublicRpcDescriptors.IsAgentPublicContract(contractId)
            ? CurrentPublicRpcDescriptors.Get(contractId)
            : RegressionTestRpcDescriptors.Get(contractId);
    }

    internal static void AssertCurrent(SunderRpcContractDescriptor descriptor)
    {
        var expected = Successors[descriptor.ContractId];
        Assert.Equal(expected.GetProperty("version").GetString(), descriptor.Version);
        Assert.Equal(expected.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
        Assert.Equal(expected.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
        Assert.Equal(expected.GetProperty("methods").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal),
            descriptor.Services.Select(value => value.ServiceId).Order(StringComparer.Ordinal));
        foreach (var service in descriptor.Services)
            Assert.Equal(expected.GetProperty("methods").GetProperty(service.ServiceId).EnumerateArray().Select(value => value.GetString()),
                service.Methods.Select(value => value.MethodId).Order(StringComparer.Ordinal));
    }

    internal static JsonSerializerOptions OwnerJson => (JsonSerializerOptions)OwnerAssembly
        .GetType("Sunder.Package.Agent.Runtime.AgentOwnerRuntimeProvider", throwOnError: true)!
        .GetNestedType("__SunderGeneratedRpcHandler", BindingFlags.NonPublic)!
        .GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static IReadOnlyDictionary<string, JsonElement> ReadSuccessors()
    {
        using var current = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot,
            "compatibility/agent-ordinary-private-successors.json")));
        Assert.Equal(1, current.RootElement.GetProperty("schemaVersion").GetInt32());
        var entries = current.RootElement.GetProperty("contracts").EnumerateArray().ToArray();
        Assert.Equal(13, entries.Length);
        return entries.ToDictionary(value => value.GetProperty("contractId").GetString()!, value => value.Clone(), StringComparer.Ordinal);
    }
}

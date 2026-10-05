using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Independent wire oracle for the loopback Host. These bytes are historical test
// evidence, not a dependency on a central Protocol assembly or a live handler.
internal static class RegressionTestRpcDescriptors
{
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();
    private static readonly Lazy<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> Descriptors = new(Read);

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> ErrorMap(SunderRpcContractDescriptor descriptor)
        => descriptor.Services.ToDictionary(service => service.ServiceId,
            service => (IReadOnlyDictionary<string, IReadOnlyList<string>>)service.Methods.ToDictionary(
                method => method.MethodId, method => method.Errors, StringComparer.Ordinal), StringComparer.Ordinal);

    internal static SunderRpcContractDescriptor Get(string contractId)
        => Descriptors.Value.TryGetValue(contractId, out var descriptor) ? descriptor
            : throw new InvalidOperationException($"The frozen Agent RPC oracle has no contract '{contractId}'.");

    private static IReadOnlyDictionary<string, SunderRpcContractDescriptor> Read()
    {
        var path = Path.Combine(RepositoryRoot, "compatibility/agent-rpc-migration-baseline.json");
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = baseline.RootElement.GetProperty("descriptors").EnumerateArray().ToArray();
        Assert.Equal(31, entries.Length);
        return entries.ToDictionary(entry => entry.GetProperty("contractId").GetString()!, entry =>
        {
            var descriptor = SunderRpcContractDescriptor.Parse(Convert.FromBase64String(
                entry.GetProperty("canonicalDescriptorBase64").GetString()!));
            Assert.Equal(entry.GetProperty("contractId").GetString(), descriptor.ContractId);
            Assert.Equal(entry.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
            Assert.Equal(entry.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
            return descriptor;
        }, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "compatibility/agent-rpc-migration-baseline.json");
            if (File.Exists(path)) return directory.FullName;
        }
        throw new InvalidOperationException("The frozen compatibility/agent-rpc-migration-baseline.json is required by the Agent regression harness.");
    }
}

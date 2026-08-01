using System.Reflection;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

public static class AgentRpcContractDescriptors
{
    private const string ResourcePrefix = "Sunder.Package.Agent.Protocol.Contracts.";
    private static readonly Lazy<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> Descriptors =
        new(LoadDescriptors, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyCollection<SunderRpcContractDescriptor> All => Descriptors.Value.Values.ToArray();

    public static SunderRpcContractDescriptor Get(string contractId)
        => Descriptors.Value.TryGetValue(contractId, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"Unknown Agent RPC contract '{contractId}'.", nameof(contractId));

    private static IReadOnlyDictionary<string, SunderRpcContractDescriptor> LoadDescriptors()
    {
        var assembly = typeof(AgentRpcContractDescriptors).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                  && name.EndsWith(".rpc.json", StringComparison.Ordinal))
            .Select(name => Parse(assembly, name))
            .ToDictionary(static descriptor => descriptor.ContractId, StringComparer.Ordinal);
    }

    private static SunderRpcContractDescriptor Parse(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Agent RPC descriptor '{resourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

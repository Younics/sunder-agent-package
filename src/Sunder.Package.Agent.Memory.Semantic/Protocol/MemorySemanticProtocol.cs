using System.Reflection;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

public static class MemorySemanticProtocol
{
    public const string ProviderId = "semantic.memory.inspector";

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Memory.Semantic.Protocol.Contracts.memory-inspector.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> InspectorDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Inspector => InspectorDescriptor.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(MemorySemanticProtocol).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

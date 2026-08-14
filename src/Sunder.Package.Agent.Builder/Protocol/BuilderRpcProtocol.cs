using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder.Protocol;

public static class BuilderRpcProtocol
{
    private const string ResourceName =
        "Sunder.Package.Agent.Builder.Protocol.builder-runtime.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> DescriptorValue =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => DescriptorValue.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(BuilderRpcProtocol).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded RPC descriptor '{ResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

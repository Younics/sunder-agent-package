using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Protocol;

public static class SubagentsOwnerProtocol
{
    public const string ProviderId = "subagents.management";

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Subagents.Protocol.Contracts.subagents-owner.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> DescriptorValue =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => DescriptorValue.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(SubagentsOwnerProtocol).Assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

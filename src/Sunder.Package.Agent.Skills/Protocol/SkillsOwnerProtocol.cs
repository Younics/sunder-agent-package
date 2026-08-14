using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Skills.Protocol;

public static class SkillsOwnerProtocol
{
    public const string ProviderId = "skills.management";

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Skills.Protocol.Contracts.skills-owner.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> DescriptorValue =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => DescriptorValue.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(SkillsOwnerProtocol).Assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

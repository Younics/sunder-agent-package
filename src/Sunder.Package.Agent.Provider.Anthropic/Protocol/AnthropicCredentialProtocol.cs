using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.Anthropic.Protocol;

public static class AnthropicCredentialProtocol
{
    private const string ResourceName =
        "Sunder.Package.Agent.Provider.Anthropic.Protocol.credential-settings.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> DescriptorValue =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => DescriptorValue.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(AnthropicCredentialProtocol).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded RPC descriptor '{ResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

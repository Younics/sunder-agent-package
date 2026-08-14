using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.Gemini.Protocol;

public static class GeminiCredentialProtocol
{
    private const string ResourceName =
        "Sunder.Package.Agent.Provider.Gemini.Protocol.credential-settings.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> DescriptorValue =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Descriptor => DescriptorValue.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        using var stream = typeof(GeminiCredentialProtocol).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded RPC descriptor '{ResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

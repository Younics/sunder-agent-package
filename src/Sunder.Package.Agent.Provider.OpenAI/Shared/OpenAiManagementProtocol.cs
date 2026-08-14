using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiManagementProtocol
{
    internal const string ProviderId = "openai.management";

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Provider.OpenAI.Contracts.openai-management.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static SunderRpcContractDescriptor Descriptor => ContractDescriptor.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(OpenAiManagementProtocol).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal static class McpManagementProtocol
{
    internal const string ProviderId = "mcp.management";
    internal const string OAuthCallbackHandlerId = "mcp.oauth.v1";
    internal const string OAuthServerIdParameter = "serverId";

    private const string DescriptorResourceName =
        "Sunder.Package.Agent.Mcp.Contracts.mcp-management.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static SunderRpcContractDescriptor Descriptor => ContractDescriptor.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(McpManagementProtocol).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

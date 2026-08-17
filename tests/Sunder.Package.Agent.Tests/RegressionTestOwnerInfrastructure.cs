using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    public IAgentOwnerClient AddOwnerRuntimeProvider(
        IAgentOwnerRuntimeProvider implementation,
        string packageId = "sunder.package.agent")
    {
        var providerCatalog = new AgentRpcCatalog(_client.ForCaller(packageId));
        _client.State.Add(
            packageId,
            AgentOwnerRuntimeRpc.Descriptor,
            implementation,
            AgentOwnerRuntimeRpc.CreateHandler(implementation),
            providerCatalog,
            AgentOwnerRuntimeRpc.ProviderId);
        return new AgentOwnerRpcClient(_client.ForCaller("sunder.package.agent.app"));
    }
}

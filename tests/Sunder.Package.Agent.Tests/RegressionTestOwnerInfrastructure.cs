extern alias AgentApp;

using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Rpc.Owner;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    public AppRuntime.IAgentOwnerClient AddOwnerRuntimeProvider(
        IAgentOwnerRuntimeProvider implementation,
        string packageId = "sunder.package.agent")
    {
        var providerCatalog = new AgentRpcCatalog(_client.ForCaller(packageId));
        var service = new OwnerRuntimeService(implementation);
        var registration = service.CreateSunderRpcProviderRegistration();
        _client.State.AddGenerated(
            packageId,
            implementation,
            registration,
            providerCatalog,
            registration.ProviderId);
        // The App owns both the operation tokens and response CLR identities. Its
        // generated client crosses the real JSON/content boundary to the Worker.
        return new AppRuntime.AgentOwnerRpcClient(_client.ForCaller(packageId));
    }
}

extern alias AgentApp;

using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Protocol;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using AgentServices = AgentApp::Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    public AgentServices.IAgentRuntimeService AddOwnerRuntimeProvider(
        AgentOwnerRuntimeProvider implementation,
        string packageId = "sunder.package.agent")
    {
        var receiver = Receiver(packageId);
        var registration = receiver.Register(implementation, "sunder.agent.owner.runtime");
        _client.State.AddGenerated(
            packageId,
            implementation,
            registration,
            EmptyProviderLifetime.Instance,
            registration.ProviderId);
        // Exercise the actual generated App service across the real Worker boundary.
        return new AgentApp::Sunder.Generated.Packages.Sunder_Package_Agent_Internal.CSunder_Agent_Owner_RuntimeClient(receiver.Client, receiver);
    }
}

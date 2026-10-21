extern alias AgentApp;

using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Protocol;
using AppRuntime = AgentApp::Sunder.Package.Agent.Runtime;
using AgentServices = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    public AgentServices.IAgentRuntimeService AddOwnerRuntimeProvider(
        AgentOwnerRuntimeProvider implementation,
        string packageId = "younics.ai.agent")
    {
        var receiver = Receiver(packageId);
        var registration = receiver.Register(implementation, "younics.ai.agent.owner.runtime");
        _client.State.AddGenerated(
            packageId,
            implementation,
            registration,
            EmptyProviderLifetime.Instance,
            registration.ProviderId);
        // Exercise the actual generated App service across the real Worker boundary.
        return new AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Internal.CYounics_Ai_Agent_Owner_RuntimeClient(receiver.Client, receiver);
    }
}

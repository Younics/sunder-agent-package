using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class AgentBehaviorServices(AgentRunControlRegistry registrations, AgentRpcCatalog catalog)
{
    internal AgentRpcProviderService<IAgentBehaviorLoop> Service { get; } = AgentBehaviorBindings.Create(registrations, catalog);
}

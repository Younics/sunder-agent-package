using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal interface IAgentToolProfileCatalog
{
    AgentProfileRecord? GetProfile(string profileId);
}

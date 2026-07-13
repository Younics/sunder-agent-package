using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentToolPresentationResolver
{
    AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request);
}

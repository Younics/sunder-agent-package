using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentExecutionScopeProvider
{
    ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default);
}

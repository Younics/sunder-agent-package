using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentInnerBehaviorLoopRuntime
{
    ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken = default);
}

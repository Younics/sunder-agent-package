using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentBehaviorLoop
{
    AgentBehaviorLoopDescriptor Descriptor { get; }

    ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime runtime,
        CancellationToken cancellationToken = default);
}

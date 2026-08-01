using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class OrchestratedAgentBehaviorLoop : IAgentBehaviorLoop
{
    public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
        SubagentConstants.OrchestratedBehaviorLoopId,
        "Orchestrated",
        "Enables task delegation to profile-selected subagents while preserving the base agent loop.",
        SubagentConstants.PackageId,
        [SubagentConstants.FeatureKind]);

    public ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime host,
        CancellationToken cancellationToken = default)
    {
        if (host is not IAgentInnerBehaviorLoopRuntime innerRuntime)
        {
            var checkpoint = host.SaveCheckpoint(AgentRunStatus.Failed, "Default Agent behavior loop is unavailable.");
            return ValueTask.FromResult(new AgentBehaviorLoopResult(checkpoint, AgentBehaviorLoopCompletionKind.Failed));
        }

        return innerRuntime.RunDefaultLoopAsync(context, cancellationToken);
    }
}

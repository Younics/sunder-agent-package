using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class OrchestratedAgentBehaviorLoop(IPackageExtensionCatalog extensionCatalog) : IAgentBehaviorLoop
{
    public AgentBehaviorLoopDescriptor Descriptor { get; } = new(
        SubagentConstants.OrchestratedBehaviorLoopId,
        "Orchestrated",
        "Enables task delegation to profile-selected subagents while preserving the base agent loop.",
        SubagentConstants.PackageId,
        ["subagents"]);

    public ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime host,
        CancellationToken cancellationToken = default)
    {
        var defaultLoop = extensionCatalog.GetExtensions(PackageExtensionPoints.BehaviorLoops)
            .FirstOrDefault(loop => !ReferenceEquals(loop, this)
                                    && string.Equals(loop.Descriptor.LoopId, AgentBehaviorLoopIds.Default, StringComparison.OrdinalIgnoreCase));
        if (defaultLoop is null)
        {
            var checkpoint = host.SaveCheckpoint(AgentRunStatus.Failed, "Default Agent behavior loop is unavailable.");
            return ValueTask.FromResult(new AgentBehaviorLoopResult(checkpoint, AgentBehaviorLoopCompletionKind.Failed));
        }

        return defaultLoop.RunAsync(context, host, cancellationToken);
    }
}

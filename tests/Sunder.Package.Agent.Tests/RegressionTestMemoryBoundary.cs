extern alias MemoryWorker;

using Sunder.Package.Agent.Contracts.Models;
using MemoryFeature = MemoryWorker::Sunder.Package.Agent.Memory.Semantic.MemorySemanticFeature;
using MemoryModels = MemoryWorker::Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

// Component scenarios keep the real memory store/services and adapt only the
// owning role's request/result snapshots at the assertion boundary.
internal static class RegressionTestMemoryBoundary
{
    internal static async ValueTask<AgentMemoryRecallResult?> RecallAsync(this MemoryFeature feature,
        AgentMemoryRecallRequest request, CancellationToken token = default)
        => AgentRoleBoundary.As<AgentMemoryRecallResult?>((await feature.RecallAsync(
            AgentRoleBoundary.As<MemoryModels.AgentMemoryRecallRequest>(request), token).ConfigureAwait(false))!);

    internal static ValueTask HandleLifecycleEventAsync(this MemoryFeature feature, AgentLifecycleEvent value, CancellationToken token = default)
        => feature.HandleLifecycleEventAsync(AgentRoleBoundary.As<MemoryModels.AgentLifecycleEvent>(value), token);

    internal static ValueTask HandleDurableLifecycleEventAsync(this MemoryFeature feature,
        AgentDurableLifecycleEventEnvelope value, CancellationToken token = default)
        => feature.HandleDurableLifecycleEventAsync(AgentRoleBoundary.As<MemoryModels.AgentDurableLifecycleEventEnvelope>(value), token);

    internal static async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(this MemoryFeature feature,
        AgentPromptContextRequest value, CancellationToken token = default)
        => AgentRoleBoundary.As<AgentPromptContextContribution?>((await feature.ContributeContextAsync(
            AgentRoleBoundary.As<MemoryModels.AgentPromptContextRequest>(value), token).ConfigureAwait(false))!);
}

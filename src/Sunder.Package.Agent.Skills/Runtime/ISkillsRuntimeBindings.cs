using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Skills.Runtime;

public interface ISkillsRuntimeBindings : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask<AgentProfileRecord?> GetProfileAsync(string? profileId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentExecutionTargetContext context, IReadOnlyList<AgentExecutionResourceDescriptor> resources,
        AgentRpcProviderHandle? selectedTarget, CancellationToken cancellationToken = default);
}

public interface ISkillsExecutionFeature
{
    ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request,
        AgentRpcProviderHandle? selectedTarget, CancellationToken cancellationToken = default);
    ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request,
        AgentRpcProviderHandle? selectedTarget, CancellationToken cancellationToken = default);
}

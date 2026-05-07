using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentExecutionResourceProvider
{
    string ProviderId { get; }

    string DisplayName { get; }

    ValueTask<IReadOnlyList<AgentExecutionResourceDescriptor>> ListResourcesAsync(
        AgentExecutionResourceRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAgentExecutionResourceResolver
{
    ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentExecutionTargetContext context,
        IReadOnlyList<AgentExecutionResourceDescriptor> resources,
        CancellationToken cancellationToken = default);
}

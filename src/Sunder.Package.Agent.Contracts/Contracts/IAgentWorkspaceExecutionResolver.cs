using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentWorkspaceExecutionResolver
{
    IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces();

    ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}

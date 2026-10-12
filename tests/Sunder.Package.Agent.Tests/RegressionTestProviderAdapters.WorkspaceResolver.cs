using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestWorkspaceResolverProvider(IAgentWorkspaceExecutionResolver resolver)
    : AgentServices.IWorkspaceExecutionResolverService
{
    public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => resolver.ListWorkspacesAsync(cancellationToken);

    public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(string workspaceId, CancellationToken cancellationToken = default)
        => resolver.ResolveAsync(workspaceId, cancellationToken);
}

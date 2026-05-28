using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentWorkspaceExecutionResolver(
    AgentWorkspaceService workspaceService,
    AgentExecutionTargetService executionTargetService) : IAgentWorkspaceExecutionResolver
{
    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
        => workspaceService.ListWorkspaces();

    public async ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workspace = workspaceService.GetWorkspace(workspaceId)
            ?? throw new InvalidOperationException("Selected workspace was not found.");
        var binding = workspaceService.ListBindings(workspace.WorkspaceId)
            .FirstOrDefault(binding => binding.IsEnabled
                                       && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Selected workspace is not bound to an execution target.");
        var target = executionTargetService.ResolveTarget(binding)
            ?? throw new InvalidOperationException("Selected workspace execution target is not available.");
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var readiness = await target.GetReadinessAsync(context, cancellationToken);
        if (readiness.Status != AgentExecutionTargetReadinessStatus.Ready)
        {
            throw new InvalidOperationException(readiness.Message);
        }

        var scope = target is IAgentExecutionScopeProvider scopeProvider
            ? await scopeProvider.GetExecutionScopeAsync(context, cancellationToken)
            : new AgentExecutionScopeDescriptor(target.Descriptor.DisplayName, []);
        if (scope.WorkspacePaths.Count == 0)
        {
            throw new InvalidOperationException("Selected workspace execution target does not expose a workspace path.");
        }

        return new AgentWorkspaceExecutionResolution(workspace, binding, target.Descriptor, scope, target);
    }
}

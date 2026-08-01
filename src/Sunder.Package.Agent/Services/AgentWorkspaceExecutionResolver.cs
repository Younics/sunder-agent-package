using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

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
        var target = executionTargetService.ResolveTargetReference(binding)
            ?? throw new InvalidOperationException("Selected workspace execution target is not available.");
        if (!target.TryAcquire(out var lease))
        {
            throw new InvalidOperationException("Selected workspace execution target is not available.");
        }
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        AgentExecutionTargetDescriptor descriptor;
        AgentExecutionScopeDescriptor scope;
        IAgentExecutionTarget executionTarget;
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetirementToken))
        {
            executionTarget = lease.Service;
            descriptor = executionTarget.Descriptor;
            var readiness = await executionTarget.GetReadinessAsync(context, invocation.Token).ConfigureAwait(false);
            if (readiness.Status != AgentExecutionTargetReadinessStatus.Ready)
            {
                throw new InvalidOperationException(readiness.Message);
            }
            scope = descriptor.SupportsFacet(AgentExecutionFacetIds.ExecutionScope)
                ? await ((IAgentExecutionScopeProvider)executionTarget).GetExecutionScopeAsync(context, invocation.Token).ConfigureAwait(false)
                : new AgentExecutionScopeDescriptor(descriptor.DisplayName, []);
        }
        if (scope.WorkspacePaths.Count == 0)
        {
            throw new InvalidOperationException("Selected workspace execution target does not expose a workspace path.");
        }

        return new AgentWorkspaceExecutionResolution(
            workspace,
            binding,
            descriptor,
            scope,
            executionTarget)
        {
            ExecutionTargetReference = target,
            ExecutionTargetHandle = target.ToHandle(),
        };
    }
}

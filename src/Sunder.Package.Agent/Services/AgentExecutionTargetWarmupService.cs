using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetWarmupService(
    AgentWorkspaceService workspaceService,
    AgentExecutionTargetService executionTargetService) : IAgentExecutionGateway
{
    public async Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
        AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var binding = workspaceService.ListBindings(workspace.WorkspaceId)
            .FirstOrDefault(binding => binding.IsEnabled
                                       && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
        if (binding is null)
        {
            return AgentExecutionTargetWarmupResult.Skipped("Workspace is not bound to an execution target.");
        }

        try
        {
            var target = await executionTargetService.ResolveTargetReferenceAsync(binding, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                return AgentExecutionTargetWarmupResult.Failed("The selected workspace is not bound to an installed execution target.");
            }

            var lease = await target.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return AgentExecutionTargetWarmupResult.Failed("The selected execution target is unavailable.");
            }
            AgentExecutionTargetReadiness readiness;
            using (lease)
            using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetirementToken))
            {
                readiness = await lease.Service.GetReadinessAsync(
                    new AgentExecutionTargetContext(null, null, workspace, binding),
                    invocation.Token).ConfigureAwait(false);
            }
            return readiness.Status == AgentExecutionTargetReadinessStatus.Ready
                ? AgentExecutionTargetWarmupResult.Ready(readiness.Message)
                : AgentExecutionTargetWarmupResult.Failed(readiness.Message);
        }
        catch (AgentPackageUnavailableException ex)
        {
            return AgentExecutionTargetWarmupResult.Failed(ex.Message);
        }
    }

    public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets()
        => executionTargetService.ListTargets();
}

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;

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
            var target = await executionTargetService.ResolveTargetAsync(binding, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                return AgentExecutionTargetWarmupResult.Failed("The selected workspace is not bound to an installed execution target.");
            }

            var readiness = await target.Service.ReadinessAsync(
                new AgentExecutionTargetContext(null, null, workspace, binding),
                cancellationToken).ConfigureAwait(false);
            return readiness.Status == AgentExecutionTargetReadinessStatus.Ready
                ? AgentExecutionTargetWarmupResult.Ready(readiness.Message)
                : AgentExecutionTargetWarmupResult.Failed(readiness.Message);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return AgentExecutionTargetWarmupResult.Failed(ex.Message);
        }
    }

    public Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(CancellationToken cancellationToken = default)
        => executionTargetService.ListTargetsAsync(cancellationToken);
}

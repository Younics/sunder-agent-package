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

        var target = executionTargetService.ResolveTargetReference(binding);
        if (target is null)
        {
            return AgentExecutionTargetWarmupResult.Failed("The selected workspace is not bound to an installed execution target.");
        }

        try
        {
            if (!target.TryAcquire(out var lease))
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentExecutionTargetWarmupResult.Failed(ex.Message);
        }
    }

    public IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets()
        => executionTargetService.ListTargets();
}

public sealed record AgentExecutionTargetWarmupResult(
    AgentExecutionTargetWarmupStatus Status,
    string Message)
{
    public static AgentExecutionTargetWarmupResult Ready(string message) => new(AgentExecutionTargetWarmupStatus.Ready, message);

    public static AgentExecutionTargetWarmupResult Skipped(string message) => new(AgentExecutionTargetWarmupStatus.Skipped, message);

    public static AgentExecutionTargetWarmupResult Failed(string message) => new(AgentExecutionTargetWarmupStatus.Failed, message);
}

public enum AgentExecutionTargetWarmupStatus
{
    Ready = 0,
    Skipped = 1,
    Failed = 2,
}

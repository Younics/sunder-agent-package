using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentExecutionTargetWarmupService(
    AgentWorkspaceService workspaceService,
    AgentExecutionTargetService executionTargetService)
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

        var target = executionTargetService.ResolveTarget(binding);
        if (target is null)
        {
            return AgentExecutionTargetWarmupResult.Failed("The selected workspace is not bound to an installed execution target.");
        }

        try
        {
            var readiness = await target.GetReadinessAsync(
                new AgentExecutionTargetContext(null, null, workspace, binding),
                cancellationToken);
            return readiness.Status == AgentExecutionTargetReadinessStatus.Ready
                ? AgentExecutionTargetWarmupResult.Ready(readiness.Message)
                : AgentExecutionTargetWarmupResult.Failed(readiness.Message);
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

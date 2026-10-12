using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Services;

[SunderService(SunderServiceExposure.Public, Key = "workspace-execution-resolver", ContractId = "sunder.agent.workspace.execution.resolver", ServiceId = "workspace-execution-resolver", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "agent.workspace.execution.resolver")]
[SunderAppUse(Enabled = false)]
public sealed partial class AgentWorkspaceExecutionResolver(
    AgentWorkspaceService workspaceService,
    AgentExecutionTargetService executionTargetService)
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
        => await workspaceService.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetWorkspaceAsync(workspaceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected workspace was not found.");
        var binding = (await workspaceService.ListBindingsAsync(workspace.WorkspaceId, cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefault(binding => binding.IsEnabled
                                       && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Selected workspace is not bound to an execution target.");
        var target = await executionTargetService.ResolveTargetAsync(binding, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected workspace execution target is not available.");
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var descriptor = await target.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
        var readiness = await target.Service.ReadinessAsync(context, cancellationToken).ConfigureAwait(false);
        if (readiness.Status != AgentExecutionTargetReadinessStatus.Ready)
        {
            throw new InvalidOperationException(readiness.Message);
        }
        var scope = descriptor.SupportsFacet(AgentExecutionFacetIds.ExecutionScope)
            ? await target.Service.ExecutionScopeAsync(context, cancellationToken).ConfigureAwait(false)
            : new AgentExecutionScopeDescriptor(descriptor.DisplayName, []);
        if (scope.WorkspacePaths.Count == 0)
        {
            throw new InvalidOperationException("Selected workspace execution target does not expose a workspace path.");
        }

        await target.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new AgentWorkspaceExecutionResolution(workspace, binding, descriptor, scope, target.Reference);
    }
}

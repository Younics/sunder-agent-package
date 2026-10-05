using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Permissions = Sunder.Generated.Rpc.PermissionSurface;
using Prompts = Sunder.Generated.Rpc.SystemPromptContributor;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestPermissionProvider(IAgentPermissionSurface surface) : Permissions.IPermissionSurfaceProvider
{
    public ValueTask<Permissions.PermissionSurfaceSnapshot> DescribeAsync(SunderRpcInvocationContext context, Permissions.Unit request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Permissions.PermissionSurfaceSnapshot
        {
            SurfaceId = surface.SurfaceId,
            DisplayName = surface.DisplayName,
            Actions = RegressionTestRpcValues.Convert<Permissions.AgentPermissionActionDescriptor[]>(surface.ListActions()),
        });
    }
}

internal sealed class RegressionTestSystemPromptProvider(IAgentSystemPromptContributor contributor) : Prompts.ISystemPromptContributorProvider
{
    public ValueTask<Prompts.AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Prompts.Unit request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Prompts.AgentRpcIdentity { Id = contributor.ContributorId, DisplayName = contributor.DisplayName });
    }

    public async ValueTask<IReadOnlyList<Prompts.AgentSystemPromptBlock>> ContributeAsync(
        SunderRpcInvocationContext context, Prompts.AgentSystemPromptRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Prompts.AgentSystemPromptBlock[]>(
            await contributor.ContributeAsync(RegressionTestRpcValues.Convert<Domain.AgentSystemPromptRequest>(request), token).ConfigureAwait(false));
}

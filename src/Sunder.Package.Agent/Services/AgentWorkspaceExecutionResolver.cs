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
        var target = executionTargetService.ResolveTargetReference(binding)
            ?? throw new InvalidOperationException("Selected workspace execution target is not available.");
        var context = new AgentExecutionTargetContext(null, null, workspace, binding);
        var resolution = await AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            async (instance, token) =>
            {
                var readiness = await instance.GetReadinessAsync(context, token).ConfigureAwait(false);
                if (readiness.Status != AgentExecutionTargetReadinessStatus.Ready)
                {
                    throw new InvalidOperationException(readiness.Message);
                }

                var scope = instance is IAgentExecutionScopeProvider scopeProvider
                    ? await scopeProvider.GetExecutionScopeAsync(context, token).ConfigureAwait(false)
                    : new AgentExecutionScopeDescriptor(target.Metadata.DisplayName, []);
                return scope;
            }).ConfigureAwait(false);
        var scope = resolution;
        if (scope.WorkspacePaths.Count == 0)
        {
            throw new InvalidOperationException("Selected workspace execution target does not expose a workspace path.");
        }

        return new AgentWorkspaceExecutionResolution(
            workspace,
            binding,
            target.Metadata,
            scope,
            new AgentExecutionTargetInvocationProxy(target))
        {
            ExecutionTargetReference = target.Reference,
        };
    }
}

internal sealed class AgentExecutionTargetInvocationProxy(
    AgentExtensionReference<IAgentExecutionTarget, AgentExecutionTargetDescriptor> target)
    : IAgentExecutionTarget
{
    public AgentExecutionTargetDescriptor Descriptor { get; } = target.Metadata;

    public ValueTask<string?> GetConfigurationGenerationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.GetConfigurationGenerationAsync(context, token));

    public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.GetReadinessAsync(context, token));

    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.GetShellAsync(context, token));

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.ResolveFileResourceAsync(context, path, token));

    public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.ExecuteShellAsync(context, request, token));

    public ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.ReadFileAsync(context, request, token));

    public ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.WriteFileAsync(context, request, token));

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
        => AgentExtensionInvocation.InvokeAsync(
            target,
            cancellationToken,
            (instance, token) => instance.DeleteFileAsync(context, request, token));
}

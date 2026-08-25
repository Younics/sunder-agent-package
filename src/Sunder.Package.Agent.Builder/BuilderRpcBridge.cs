using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderRpcExecutor
{
    private const int MaximumOutputCharacters = 512 * 1024;

    internal static async ValueTask<BuilderRpcResponse> ExecuteAsync(
        IAgentWorkspaceExecutionResolver resolver,
        BuilderRpcRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind == BuilderRpcOperationKind.ListWorkspaces)
        {
            return new BuilderRpcResponse(Workspaces: await resolver
                .ListWorkspacesAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        var workspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
            ? throw Domain("agent.builder.invalid-request", "Workspace id is required.")
            : request.WorkspaceId;
        var resolution = await resolver.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var context = new AgentExecutionTargetContext(
            null,
            null,
            resolution.Workspace,
            resolution.Binding);
        var targetReference = resolution.ExecutionTargetReference
            ?? throw new InvalidOperationException(
                "Workspace resolution did not retain its exact execution-target activation.");
        if (!targetReference.TryAcquire(out var targetLease))
        {
            throw Domain(
                "agent.builder.execution-target-unavailable",
                "The selected execution-target package is unavailable.");
        }

        using (targetLease)
        {
            using var targetInvocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                targetLease.RetirementToken);
            var target = targetLease.Service;
            var invocationToken = targetInvocation.Token;
            BuilderRpcResponse response;
            try
            {
                switch (request.Kind)
                {
                    case BuilderRpcOperationKind.ResolveWorkspace:
                        var shell = await target
                            .GetShellAsync(context, invocationToken)
                            .ConfigureAwait(false);
                        response = new BuilderRpcResponse(Execution: new BuilderWorkspaceExecutionProjection(
                            resolution.Workspace,
                            resolution.Binding,
                            resolution.Target,
                            resolution.Scope,
                            shell,
                            AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.PathMapping),
                            AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.PathEnvironment)));
                        break;
                    case BuilderRpcOperationKind.ExecuteProcess:
                        if (!AgentExecutionTargetRpc.SupportsFacet(
                                target,
                                AgentExecutionFacetIds.ProcessExecution)
                            || target is not IAgentProcessExecutionTarget processTarget)
                        {
                            throw Domain(
                                "agent.builder.unsupported-operation",
                                "Package Builder requires the execution target's structured process-execution facet.");
                        }
                        var processResult = await processTarget.ExecuteProcessAsync(
                            context,
                            new AgentProcessCommandRequest(
                                Require(request.FileName, "Process file name"),
                                request.Arguments ?? [],
                                request.WorkingDirectory,
                                request.TimeoutSeconds),
                            invocationToken).ConfigureAwait(false);
                        response = new BuilderRpcResponse(Process: ProjectProcessResult(processResult));
                        break;
                    case BuilderRpcOperationKind.ExecuteShell:
                        var shellResult = await target.ExecuteShellAsync(
                            context,
                            new AgentShellCommandRequest(
                                Require(request.Command, "Shell command"),
                                request.WorkingDirectory,
                                request.TimeoutSeconds),
                            invocationToken).ConfigureAwait(false);
                        response = new BuilderRpcResponse(Process: ProjectProcessResult(shellResult));
                        break;
                    case BuilderRpcOperationKind.MapToHostPath:
                        if (!resolution.Target.SupportsFacet(AgentExecutionFacetIds.PathMapping)
                            || target is not IAgentExecutionPathMapper mapper)
                        {
                            throw Domain(
                                "agent.builder.unsupported-operation",
                                "The selected execution target does not map execution paths to host paths.");
                        }
                        response = new BuilderRpcResponse(PathMapping: await mapper.MapToHostPathAsync(
                            context,
                            Require(request.ExecutionPath, "Execution path"),
                            invocationToken).ConfigureAwait(false));
                        break;
                    case BuilderRpcOperationKind.AddPathEntry:
                        if (resolution.Target.SupportsFacet(AgentExecutionFacetIds.PathEnvironment)
                            && target is IAgentExecutionPathEnvironment pathEnvironment)
                        {
                            await pathEnvironment.AddPathEntryAsync(
                                context,
                                Require(request.ExecutionPath, "Execution path"),
                                invocationToken).ConfigureAwait(false);
                        }
                        response = new BuilderRpcResponse();
                        break;
                    default:
                        throw Domain(
                            "agent.builder.invalid-request",
                            "Unknown Package Builder RPC operation.");
                }
            }
            catch (OperationCanceledException exception) when (
                targetLease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw Domain(
                    "agent.builder.execution-target-unavailable",
                    "The selected execution-target package became unavailable during invocation.",
                    exception);
            }

            if (targetLease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw Domain(
                    "agent.builder.execution-target-unavailable",
                    "The selected execution-target package became unavailable during invocation.");
            }

            return response;
        }
    }

    private static BuilderProcessResult ProjectProcessResult(AgentShellCommandResult result)
    {
        var output = Truncate(result.Output, MaximumOutputCharacters);
        return new BuilderProcessResult(
            result.ExitCode,
            output,
            string.Empty,
            result.WasTruncated || output.Length != result.Output.Length);
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw Domain("agent.builder.invalid-request", $"{name} is required.")
            : value;

    private static BuilderRpcDomainException Domain(
        string code,
        string message,
        Exception? innerException = null)
        => new(code, message, innerException);

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

}

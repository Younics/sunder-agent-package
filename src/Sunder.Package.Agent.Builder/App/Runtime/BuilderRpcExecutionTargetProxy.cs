using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Builder;

internal sealed class BuilderRpcExecutionTargetProxy(
    IBuilderRpcGateway rpc,
    BuilderWorkspaceExecutionProjection projection)
    : IAgentProcessExecutionTarget, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment
{
    public AgentExecutionTargetDescriptor Descriptor => projection.Target;

    public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentExecutionTargetReadiness(
            Descriptor.TargetKind,
            Descriptor.TargetId,
            AgentExecutionTargetReadinessStatus.Ready,
            "Execution target was resolved by Runtime."));

    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(projection.Shell);

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
        => ToShellResult((await rpc.InvokeAsync(new BuilderRpcRequest(
            BuilderRpcOperationKind.ExecuteProcess,
            projection.Workspace.WorkspaceId,
            request.FileName,
            request.Arguments,
            WorkingDirectory: request.WorkingDirectory,
            TimeoutSeconds: request.TimeoutSeconds ?? 600), cancellationToken).ConfigureAwait(false)).Process);

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
        => ToShellResult((await rpc.InvokeAsync(new BuilderRpcRequest(
            BuilderRpcOperationKind.ExecuteShell,
            projection.Workspace.WorkspaceId,
            Command: request.Command,
            WorkingDirectory: request.WorkingDirectory,
            TimeoutSeconds: request.TimeoutSeconds ?? 600), cancellationToken).ConfigureAwait(false)).Process);

    public async ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
        => (await rpc.InvokeAsync(new BuilderRpcRequest(
            BuilderRpcOperationKind.MapToHostPath,
            projection.Workspace.WorkspaceId,
            ExecutionPath: executionPath), cancellationToken).ConfigureAwait(false)).PathMapping
           ?? throw new InvalidDataException("Runtime did not return a path mapping.");

    public ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public async ValueTask AddPathEntryAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
        => _ = await rpc.InvokeAsync(new BuilderRpcRequest(
            BuilderRpcOperationKind.AddPathEntry,
            projection.Workspace.WorkspaceId,
            ExecutionPath: executionPath), cancellationToken).ConfigureAwait(false);

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    public ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    public ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Package Builder does not expose file operations through its Runtime proxy.");

    private static AgentShellCommandResult ToShellResult(BuilderProcessResult? result)
        => result is null
            ? throw new InvalidDataException("Runtime did not return a process result.")
            : new AgentShellCommandResult(
                result.ExitCode,
                result.CombinedOutput,
                WasTruncated: result.WasTruncated);
}

using Sunder.Package.Agent.Builder.Protocol.Generated;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderRpcWireMapper
{
    internal static BuilderRpcRequest ToDomain(BuilderExecuteRequest request)
        => new(
            request.Kind switch
            {
                BuilderOperation.ListWorkspaces => BuilderRpcOperationKind.ListWorkspaces,
                BuilderOperation.ResolveWorkspace => BuilderRpcOperationKind.ResolveWorkspace,
                BuilderOperation.ExecuteProcess => BuilderRpcOperationKind.ExecuteProcess,
                BuilderOperation.ExecuteShell => BuilderRpcOperationKind.ExecuteShell,
                BuilderOperation.MapToHostPath => BuilderRpcOperationKind.MapToHostPath,
                BuilderOperation.AddPathEntry => BuilderRpcOperationKind.AddPathEntry,
                _ => throw new InvalidDataException("The Builder RPC operation is invalid."),
            },
            request.WorkspaceId,
            request.FileName,
            request.Arguments,
            request.Command,
            request.WorkingDirectory,
            checked((int)request.TimeoutSeconds),
            request.ExecutionPath);

    internal static BuilderExecuteRequest ToWire(BuilderRpcRequest request)
        => new()
        {
            Kind = request.Kind switch
            {
                BuilderRpcOperationKind.ListWorkspaces => BuilderOperation.ListWorkspaces,
                BuilderRpcOperationKind.ResolveWorkspace => BuilderOperation.ResolveWorkspace,
                BuilderRpcOperationKind.ExecuteProcess => BuilderOperation.ExecuteProcess,
                BuilderRpcOperationKind.ExecuteShell => BuilderOperation.ExecuteShell,
                BuilderRpcOperationKind.MapToHostPath => BuilderOperation.MapToHostPath,
                BuilderRpcOperationKind.AddPathEntry => BuilderOperation.AddPathEntry,
                _ => throw new InvalidDataException("The Builder RPC operation is invalid."),
            },
            WorkspaceId = request.WorkspaceId,
            FileName = request.FileName,
            Arguments = request.Arguments,
            Command = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            TimeoutSeconds = request.TimeoutSeconds,
            ExecutionPath = request.ExecutionPath,
        };
}

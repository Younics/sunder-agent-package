using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder.Rpc;

internal enum BuilderOperation
{
    [JsonStringEnumMemberName("list-workspaces")] ListWorkspaces,
    [JsonStringEnumMemberName("resolve-workspace")] ResolveWorkspace,
    [JsonStringEnumMemberName("execute-process")] ExecuteProcess,
    [JsonStringEnumMemberName("execute-shell")] ExecuteShell,
    [JsonStringEnumMemberName("map-to-host-path")] MapToHostPath,
    [JsonStringEnumMemberName("add-path-entry")] AddPathEntry,
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal readonly record struct WorkspaceId
{
    public required string Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal readonly record struct ExecutableName
{
    public required string Value { get; init; }
}

[SunderRpcTransparent]
internal readonly record struct ProcessArgument
{
    public required string Value { get; init; }
}

[SunderRpcTransparent]
internal sealed class ProcessArguments
{
    public required IReadOnlyList<ProcessArgument> Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal readonly record struct ShellCommand
{
    public required string Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
internal readonly record struct ExecutionPath
{
    public required string Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "1")]
internal readonly record struct TimeoutSeconds
{
    public required long Value { get; init; }
}

internal sealed class BuilderExecuteRequest
{
    public required BuilderOperation Kind { get; init; }
    public SunderRpcOptional<WorkspaceId> WorkspaceId { get; init; }
    public SunderRpcOptional<ExecutableName> FileName { get; init; }
    public SunderRpcOptional<ProcessArguments> Arguments { get; init; }
    public SunderRpcOptional<ShellCommand> Command { get; init; }
    public SunderRpcOptional<ExecutionPath> WorkingDirectory { get; init; }
    public required TimeoutSeconds TimeoutSeconds { get; init; }
    public SunderRpcOptional<ExecutionPath> ExecutionPath { get; init; }

    internal BuilderRpcRequest ToDomain()
        => new(
            Kind switch
            {
                BuilderOperation.ListWorkspaces => BuilderRpcOperationKind.ListWorkspaces,
                BuilderOperation.ResolveWorkspace => BuilderRpcOperationKind.ResolveWorkspace,
                BuilderOperation.ExecuteProcess => BuilderRpcOperationKind.ExecuteProcess,
                BuilderOperation.ExecuteShell => BuilderRpcOperationKind.ExecuteShell,
                BuilderOperation.MapToHostPath => BuilderRpcOperationKind.MapToHostPath,
                BuilderOperation.AddPathEntry => BuilderRpcOperationKind.AddPathEntry,
                _ => throw new InvalidDataException("The Builder RPC operation is invalid."),
            },
            WorkspaceId.IsPresent ? WorkspaceId.Value.Value : null,
            FileName.IsPresent ? FileName.Value.Value : null,
            Arguments.IsPresent ? Arguments.Value.Value.Select(item => item.Value).ToArray() : null,
            Command.IsPresent ? Command.Value.Value : null,
            WorkingDirectory.IsPresent ? WorkingDirectory.Value.Value : null,
            checked((int)TimeoutSeconds.Value),
            ExecutionPath.IsPresent ? ExecutionPath.Value.Value : null);
}

[SunderRpcTransparent]
internal sealed class ContentReference
{
    public required SunderRpcContentReference Value { get; init; }
}

internal sealed class BuilderResultContent
{
    public required ContentReference Content { get; init; }
}

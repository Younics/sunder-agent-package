using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderRpcPayloadLimits
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int ResponseSafetyBytes = 64 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static int GetSerializedSize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;
}

internal enum BuilderRpcOperationKind
{
    ListWorkspaces,
    ResolveWorkspace,
    ExecuteProcess,
    ExecuteShell,
    MapToHostPath,
    AddPathEntry,
}

internal sealed record BuilderRpcRequest(
    BuilderRpcOperationKind Kind,
    string? WorkspaceId = null,
    string? FileName = null,
    IReadOnlyList<string>? Arguments = null,
    string? Command = null,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 600,
    string? ExecutionPath = null);

internal sealed record BuilderRpcResponse(
    IReadOnlyList<AgentWorkspaceRecord>? Workspaces = null,
    BuilderWorkspaceExecutionProjection? Execution = null,
    BuilderProcessResult? Process = null,
    AgentExecutionPathMapping? PathMapping = null);

internal sealed record BuilderWorkspaceExecutionProjection(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    AgentExecutionShellDescriptor Shell,
    bool SupportsPathMapping,
    bool SupportsPathEnvironment);

public sealed record BuilderProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasTruncated = false)
{
    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

internal interface IBuilderRpcGateway
{
    ValueTask<BuilderRpcResponse> InvokeAsync(
        BuilderRpcRequest request,
        CancellationToken cancellationToken = default);
}

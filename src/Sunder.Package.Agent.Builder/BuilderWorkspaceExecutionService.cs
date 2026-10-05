using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderWorkspaceExecutionService
{
    private readonly IBuilderRpcGateway _rpc;

    public BuilderWorkspaceExecutionService()
        : this(EmptyBuilderRpcGateway.Instance)
    {
    }

    private BuilderWorkspaceExecutionService(IBuilderRpcGateway rpc)
    {
        _rpc = rpc;
    }

    public async Task<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = Task.Run(
            () => _rpc.InvokeAsync(
                new BuilderRpcRequest(BuilderRpcOperationKind.ListWorkspaces),
                cancellationToken).AsTask(),
            CancellationToken.None);
        try
        {
            var response = await invocation.WaitAsync(cancellationToken).ConfigureAwait(false);
            return response.Workspaces ?? [];
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !PresentationExceptionPolicy.IsFatal(exception))
        {
            _ = ObserveDetachedInvocationAsync(invocation, cancellationToken);
            throw;
        }
    }

    private static async Task ObserveDetachedInvocationAsync(
        Task invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            await invocation.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && !PresentationExceptionPolicy.IsFatal(exception))
        {
        }
        catch (Exception exception) when (
            PresentationExceptionPolicy.IsExpectedOperationFailure(exception))
        {
        }
        catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
        {
            Trace.TraceError(
                $"A detached Package Builder workspace-list operation failed: {exception}");
        }
        catch (Exception exception)
        {
            ThreadPool.QueueUserWorkItem(
                static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
                exception);
        }
    }

    public async ValueTask<BuilderWorkspaceExecution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var resolution = (await _rpc.InvokeAsync(new BuilderRpcRequest(
                BuilderRpcOperationKind.ResolveWorkspace,
                workspaceId), cancellationToken).ConfigureAwait(false)).Execution
            ?? throw new InvalidDataException("Runtime did not return workspace execution details.");
        var context = new AgentExecutionTargetContext(null, null, resolution.Workspace, resolution.Binding);
        var proxy = new BuilderRpcExecutionTargetProxy(_rpc, resolution);
        return new BuilderWorkspaceExecution(
            resolution.Workspace,
            resolution.Binding,
            resolution.Target,
            resolution.Scope,
            proxy,
            context,
            resolution.Shell,
            resolution.SupportsPathMapping ? proxy : null,
            resolution.SupportsPathEnvironment ? proxy : null);
    }
}

internal sealed class EmptyBuilderRpcGateway : IBuilderRpcGateway
{
    internal static EmptyBuilderRpcGateway Instance { get; } = new();

    public ValueTask<BuilderRpcResponse> InvokeAsync(
        BuilderRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request.Kind == BuilderRpcOperationKind.ListWorkspaces
            ? ValueTask.FromResult(new BuilderRpcResponse(Workspaces: []))
            : ValueTask.FromException<BuilderRpcResponse>(
                new InvalidOperationException("Agent workspace execution service is unavailable."));
    }
}

public sealed record BuilderWorkspaceExecution(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    IAgentExecutionTarget ExecutionTarget,
    AgentExecutionTargetContext Context,
    AgentExecutionShellDescriptor Shell,
    IAgentExecutionPathMapper? PathMapper,
    IAgentExecutionPathEnvironment? PathEnvironment)
{
    public bool IsWindows => Shell.SyntaxKind is AgentShellSyntaxKinds.PowerShell or AgentShellSyntaxKinds.Cmd;

    public bool IsPosix => Shell.SyntaxKind == AgentShellSyntaxKinds.PosixSh;

    public string DefaultExecutionRoot => Scope.DefaultWorkingDirectory ?? Scope.WorkspacePaths.First();

    public async ValueTask<BuilderProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        if (!Target.SupportsFacet(AgentExecutionFacetIds.ProcessExecution)
            || ExecutionTarget is not IAgentProcessExecutionTarget processTarget)
        {
            throw new NotSupportedException(
                "Package Builder requires the execution target's structured process-execution facet.");
        }

        var result = await processTarget.ExecuteProcessAsync(
            Context,
            new AgentProcessCommandRequest(fileName, arguments, workingDirectory, timeoutSeconds),
            cancellationToken);
        return new BuilderProcessResult(result.ExitCode, result.Output, string.Empty, result.WasTruncated);
    }

    public async ValueTask<BuilderProcessResult> RunShellAsync(
        string command,
        string? workingDirectory = null,
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecutionTarget.ExecuteShellAsync(
            Context,
            new AgentShellCommandRequest(command, workingDirectory, timeoutSeconds),
            cancellationToken);
        return new BuilderProcessResult(result.ExitCode, result.Output, string.Empty, result.WasTruncated);
    }

    public async ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        if (PathMapper is null)
        {
            return new AgentExecutionPathMapping(executionPath, executionPath, IsInsideWorkspacePath(executionPath));
        }

        return await PathMapper.MapToHostPathAsync(Context, executionPath, cancellationToken);
    }

    public string ResolveExecutionWorkspacePath(AgentWorkspacePathRecord workspacePath)
    {
        var selectedHostPath = NormalizeHostPath(workspacePath.HostPath);
        var hostPaths = Workspace.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path.HostPath))
            .OrderBy(path => path.SortOrder)
            .Select(path => NormalizeHostPath(path.HostPath))
            .Distinct(GetHostPathStringComparer())
            .ToArray();
        var index = Array.FindIndex(hostPaths, path => string.Equals(path, selectedHostPath, GetHostPathStringComparison()));
        if (index >= 0 && index < Scope.WorkspacePaths.Count)
        {
            return Scope.WorkspacePaths[index];
        }

        if (PathMapper is null)
        {
            return selectedHostPath;
        }

        throw new InvalidOperationException("Selected workspace path is not available in the execution target.");
    }

    public async ValueTask AddPathEntryAsync(string executionPath, CancellationToken cancellationToken = default)
    {
        if (PathEnvironment is null)
        {
            return;
        }

        await PathEnvironment.AddPathEntryAsync(Context, executionPath, cancellationToken);
    }

    public string CombinePath(string root, string leaf)
        => UsesWindowsPaths(root) ? root.TrimEnd('\\', '/') + "\\" + leaf : root.TrimEnd('/') + "/" + leaf;

    private bool IsInsideWorkspacePath(string path)
        => Scope.WorkspacePaths.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private bool UsesWindowsPaths(string path)
        => IsWindows || path.Contains('\\') || path.Contains(':');

    private static string NormalizeHostPath(string hostPath)
        => Path.GetFullPath(Environment.ExpandEnvironmentVariables(hostPath.Trim()));

    private static IEqualityComparer<string> GetHostPathStringComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison GetHostPathStringComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string QuotePosix(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

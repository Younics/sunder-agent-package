using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Generated.Packages.Sunder_Package_Agent_Builder_Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderWorkspaceExecutionService
{
    private readonly IBuilderRuntimeService? _runtime;

    public BuilderWorkspaceExecutionService() { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public BuilderWorkspaceExecutionService(IBuilderRuntimeService runtime) => _runtime = runtime;

    public async Task<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime is null) return [];
        var invocation = Task.Run(
            () => BuilderContent.ReadAsync<IReadOnlyList<AgentWorkspaceRecord>>(_runtime.ListWorkspacesAsync(cancellationToken), cancellationToken).AsTask(),
            CancellationToken.None);
        try
        {
            return await invocation.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        if (_runtime is null) throw new InvalidOperationException("Agent workspace execution service is unavailable.");
        var resolution = await BuilderContent.ReadAsync<BuilderWorkspaceExecutionProjection>(
            _runtime.ResolveWorkspaceAsync(workspaceId, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new BuilderWorkspaceExecution(
            resolution.Workspace,
            resolution.Binding,
            resolution.Target,
            resolution.Scope,
            resolution.Shell,
            resolution.SupportsPathMapping,
            resolution.SupportsPathEnvironment,
            _runtime);
    }
}

public sealed record BuilderWorkspaceExecution(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    AgentExecutionShellDescriptor Shell,
    bool SupportsPathMapping,
    bool SupportsPathEnvironment,
    IBuilderRuntimeService Runtime)
{
    public bool IsWindows => Shell.SyntaxKind is "powershell" or "cmd";

    public bool IsPosix => Shell.SyntaxKind == "posix-sh";

    public string DefaultExecutionRoot => Scope.DefaultWorkingDirectory ?? Scope.WorkspacePaths.First();

    public async ValueTask<BuilderProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        if (!Target.Facets.Contains(AgentExecutionFacetIds.ProcessExecution, StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                "Package Builder requires the execution target's structured process-execution facet.");
        }

        return await BuilderContent.ReadAsync<BuilderProcessResult>(Runtime.ExecuteProcessAsync(Workspace.WorkspaceId,
            new AgentProcessCommandRequest { FileName = fileName, Arguments = arguments, WorkingDirectory = workingDirectory, TimeoutSeconds = timeoutSeconds },
            cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BuilderProcessResult> RunShellAsync(
        string command,
        string? workingDirectory = null,
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        return await BuilderContent.ReadAsync<BuilderProcessResult>(Runtime.ExecuteShellAsync(Workspace.WorkspaceId,
            new AgentShellCommandRequest { Command = command, WorkingDirectory = workingDirectory, TimeoutSeconds = timeoutSeconds },
            cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsPathMapping)
        {
            return new AgentExecutionPathMapping { ExecutionPath = executionPath, HostPath = executionPath, IsInsideAllowedRoot = IsInsideWorkspacePath(executionPath) };
        }

        return await Runtime.MapToHostPathAsync(Workspace.WorkspaceId, executionPath, cancellationToken);
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

        if (!SupportsPathMapping)
        {
            return selectedHostPath;
        }

        throw new InvalidOperationException("Selected workspace path is not available in the execution target.");
    }

    public async ValueTask AddPathEntryAsync(string executionPath, CancellationToken cancellationToken = default)
    {
        if (!SupportsPathEnvironment)
        {
            return;
        }

        await Runtime.AddPathEntryAsync(Workspace.WorkspaceId, executionPath, cancellationToken);
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

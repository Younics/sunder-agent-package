using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Builder;

public sealed record BuilderProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasTruncated = false)
{
    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

public sealed class BuilderWorkspaceExecutionService
{
    private readonly IBuilderRuntimeGateway _runtime;

    public BuilderWorkspaceExecutionService(IPackageRuntimeClient runtimeClient)
        : this(new BuilderAppRuntimeGateway(runtimeClient))
    {
    }

    public BuilderWorkspaceExecutionService(IAgentWorkspaceExecutionResolver resolver)
        : this(new BuilderLocalRuntimeGateway(resolver))
    {
    }

    public BuilderWorkspaceExecutionService()
        : this(EmptyBuilderRuntimeGateway.Instance)
    {
    }

    private BuilderWorkspaceExecutionService(IBuilderRuntimeGateway runtime)
    {
        _runtime = runtime;
    }

    public async Task<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = Task.Run(
            () => _runtime.InvokeAsync(
                new BuilderRuntimeRequest(BuilderRuntimeOperationKind.ListWorkspaces),
                cancellationToken).AsTask(),
            CancellationToken.None);
        var response = await invocation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return response.Workspaces ?? [];
    }

    public async ValueTask<BuilderWorkspaceExecution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var resolution = (await _runtime.InvokeAsync(new BuilderRuntimeRequest(
                BuilderRuntimeOperationKind.ResolveWorkspace,
                workspaceId), cancellationToken).ConfigureAwait(false)).Execution
            ?? throw new InvalidDataException("Runtime did not return workspace execution details.");
        var context = new AgentExecutionTargetContext(null, null, resolution.Workspace, resolution.Binding);
        var proxy = new BuilderRuntimeExecutionTargetProxy(_runtime, resolution);
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

internal sealed class EmptyBuilderRuntimeGateway : IBuilderRuntimeGateway
{
    internal static EmptyBuilderRuntimeGateway Instance { get; } = new();

    public ValueTask<BuilderRuntimeResponse> InvokeAsync(
        BuilderRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request.Kind == BuilderRuntimeOperationKind.ListWorkspaces
            ? ValueTask.FromResult(new BuilderRuntimeResponse(Workspaces: []))
            : ValueTask.FromException<BuilderRuntimeResponse>(
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
        var result = ExecutionTarget is IAgentProcessExecutionTarget processTarget
            ? await processTarget.ExecuteProcessAsync(
                Context,
                new AgentProcessCommandRequest(fileName, arguments, workingDirectory, timeoutSeconds),
                cancellationToken)
            : await ExecutionTarget.ExecuteShellAsync(
                Context,
                new AgentShellCommandRequest(BuildCommand(fileName, arguments), workingDirectory, timeoutSeconds),
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

    private string BuildCommand(string fileName, IReadOnlyList<string> arguments)
    {
        if (IsWindows)
        {
            return string.Join(' ', new[] { QuoteWindows(fileName) }.Concat(arguments.Select(QuoteWindows)));
        }

        return string.Join(' ', new[] { QuotePosix(fileName) }.Concat(arguments.Select(QuotePosix)));
    }

    public static string QuotePosix(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string QuoteWindows(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value)
        {
            if (ch is '"' or '`')
            {
                builder.Append('`');
            }

            builder.Append(ch);
        }

        return builder.Append('"').ToString();
    }
}

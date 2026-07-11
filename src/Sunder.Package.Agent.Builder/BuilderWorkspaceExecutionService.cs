using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public sealed record BuilderProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

public sealed class BuilderWorkspaceExecutionService(IPackageExtensionCatalog extensionCatalog)
{
    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
        => ResolveResolver()?.ListWorkspaces() ?? [];

    public async ValueTask<BuilderWorkspaceExecution> ResolveAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var resolver = ResolveResolver()
            ?? throw new InvalidOperationException("Agent workspace execution service is unavailable.");
        var resolution = await resolver.ResolveAsync(workspaceId, cancellationToken);
        var context = new AgentExecutionTargetContext(null, null, resolution.Workspace, resolution.Binding);
        var shell = await resolution.ExecutionTarget.GetShellAsync(context, cancellationToken);
        return new BuilderWorkspaceExecution(
            resolution.Workspace,
            resolution.Binding,
            resolution.Target,
            resolution.Scope,
            resolution.ExecutionTarget,
            context,
            shell,
            resolution.ExecutionTarget as IAgentExecutionPathMapper,
            resolution.ExecutionTarget as IAgentExecutionPathEnvironment);
    }

    private IAgentWorkspaceExecutionResolver? ResolveResolver()
        => extensionCatalog.GetExtensions(PackageExtensionPoints.WorkspaceExecutionResolvers).FirstOrDefault();
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
        return new BuilderProcessResult(result.ExitCode, result.Output, string.Empty);
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
        return new BuilderProcessResult(result.ExitCode, result.Output, string.Empty);
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

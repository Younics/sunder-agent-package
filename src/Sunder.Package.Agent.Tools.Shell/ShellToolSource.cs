using System.Text.Json;
using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Shell;

public sealed class ShellToolSource(IPackageExtensionCatalog extensionCatalog)
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentToolPresentationResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly AgentToolDescriptor Descriptor = new(
        "shell",
        "Shell Command",
        ShellDescription,
        IsReadOnly: false,
        ArgumentsJsonSchema: """
        {"type":"object","properties":{"command":{"type":"string"},"workingDirectory":{"type":"string"},"timeoutSeconds":{"type":"integer"}},"required":["command"],"additionalProperties":false}
        """,
        SourceKind: "workspace",
        SourceId: "shell",
        SourceDisplayName: "Workspace Shell",
        Aliases: ["bash"],
        RuntimeInstructions: ShellInstructions,
        Priority: AgentToolPriority.Low);

    public string SourceId => "workspace-shell";

    public string DisplayName => "Workspace Shell";

    public string SourceKind => "workspace";

    public string SurfaceId => "shell";

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!IsShellToolId(request.ToolId))
        {
            return null;
        }

        ShellArgs? args = null;
        try
        {
            args = JsonSerializer.Deserialize<ShellArgs>(request.ArgumentsJson, JsonOptions);
        }
        catch
        {
        }

        var command = args?.Command;
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? CompactCommand(command),
            DetailMarkdown: BuildShellDetailMarkdown(args, request.ArgumentsJson),
            OutputText: request.TextContent);
    }

    public async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.Workspace is null || context.ExecutionBinding is null)
        {
            return [Descriptor];
        }

        var shell = await target.GetShellAsync(new AgentExecutionTargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding), cancellationToken);
        return [Descriptor with
        {
            Description = ShellDescription + " " + shell.Description,
            RuntimeInstructions = ShellInstructions + Environment.NewLine + Environment.NewLine + "Selected executor shell:" + Environment.NewLine + shell.Description,
        }];
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        if (!IsShellToolId(toolId))
        {
            return null;
        }

        if (context.Workspace is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "Shell tools require a selected workspace.");
        }

        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.ExecutionBinding is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "The selected workspace is not bound to an installed execution target.");
        }

        var readiness = await target.GetReadinessAsync(new AgentExecutionTargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding), cancellationToken);
        return readiness.Status == AgentExecutionTargetReadinessStatus.Ready && target.Descriptor.SupportsShell
            ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Workspace shell is ready.")
            : new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, readiness.Message);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.Workspace is null)
        {
            return Error(request.ToolId, "Shell tools require a selected workspace.", "shell-workspace-required");
        }

        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.ExecutionBinding is null)
        {
            return Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "shell-target-required");
        }

        var args = JsonSerializer.Deserialize<ShellArgs>(request.ArgumentsJson, JsonOptions)
            ?? throw new InvalidOperationException("Shell arguments were empty or invalid.");
        var result = await target.ExecuteShellAsync(
            new AgentExecutionTargetContext(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding, context.AllowOutsideConfiguredScope),
            new AgentShellCommandRequest(args.Command, args.WorkingDirectory, args.TimeoutSeconds),
            cancellationToken);

        var content = string.IsNullOrWhiteSpace(result.Output)
            ? $"Command exited with code {result.ExitCode} and no output."
            : result.Output;
        return new AgentToolResult(
            request.ToolId,
            result.TimedOut ? "Shell command timed out" : $"Shell command exited with code {result.ExitCode}",
            Content: content,
            WasTruncated: result.WasTruncated,
            IsError: result.ExitCode != 0,
            ErrorCode: result.ExitCode == 0 ? null : result.TimedOut ? AgentToolResultErrorCodes.ShellTimeout : AgentToolResultErrorCodes.ShellNonZeroExit,
            BackendId: $"{target.Descriptor.TargetKind}:{target.Descriptor.TargetId}");
    }

    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? command = null;
        try
        {
            command = JsonSerializer.Deserialize<ShellArgs>(request.ArgumentsJson, JsonOptions)?.Command;
        }
        catch
        {
        }

        return ValueTask.FromResult<AgentPermissionRequest?>(new AgentPermissionRequest(
            "shell.execute",
            AgentPermissionBoundaryIds.SelectedExecutionTarget,
            string.IsNullOrWhiteSpace(command) ? "Execute shell command" : command,
            ToolId: request.ToolId,
            Command: command,
            WorkspaceId: context.Workspace?.WorkspaceId,
            BindingId: context.ExecutionBinding?.BindingId,
            IsMutation: true));
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        =>
        [
            new("shell.execute", "Execute shell commands", "Run shell commands inside the selected workspace execution target.",
            [
                new(AgentPermissionBoundaryIds.SelectedExecutionTarget, "Commands in selected workspace target", "Commands run by the selected execution target and working directory.", AgentPermissionDecision.Ask),
            ]),
        ];

    private IAgentExecutionTarget? ResolveTarget(AgentWorkspaceBindingRecord? binding)
        => extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .FirstOrDefault(target => binding is not null
                                      && (string.Equals(target.Descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(target.Descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase)));

    private static bool IsShellToolId(string toolId)
        => string.Equals(toolId, Descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
           || (Descriptor.Aliases?.Any(alias => string.Equals(alias, toolId, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static AgentToolResult Error(string toolId, string message, string code)
        => new(toolId, message, Content: $"### Shell tool failed\n\n{message}", IsError: true, ErrorCode: code);

    private static string? CompactCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var normalized = command.Replace("\r\n", "\n").Trim();
        var lines = normalized.Split('\n');
        if (lines.Length > 2)
        {
            return $"command: {FormatCount(lines.Length, "line")}";
        }

        return normalized.Length > 160
            ? $"command: {FormatCount(normalized.Length, "char")}"
            : normalized;
    }

    private static string BuildShellDetailMarkdown(ShellArgs? args, string argumentsJson)
    {
        if (args is null || string.IsNullOrWhiteSpace(args.Command))
        {
            return BuildFencedMarkdown("Arguments", "json", argumentsJson);
        }

        var builder = new StringBuilder();
        builder.AppendLine("**Command**");
        builder.AppendLine("```sh");
        builder.AppendLine(args.Command.Trim());
        builder.AppendLine("```");
        if (!string.IsNullOrWhiteSpace(args.WorkingDirectory) || args.TimeoutSeconds is not null)
        {
            builder.AppendLine();
            builder.AppendLine("**Options**");
            if (!string.IsNullOrWhiteSpace(args.WorkingDirectory))
            {
                builder.Append("- Working directory: `").Append(args.WorkingDirectory).AppendLine("`");
            }

            if (args.TimeoutSeconds is not null)
            {
                builder.Append("- Timeout: `").Append(args.TimeoutSeconds.Value).AppendLine("s`");
            }
        }

        return builder.ToString().Trim();
    }

    private static string BuildFencedMarkdown(string title, string language, string content)
    {
        var builder = new StringBuilder();
        builder.Append("**").Append(title).AppendLine("**");
        builder.Append("```").AppendLine(language);
        builder.AppendLine(content.Trim());
        builder.AppendLine("```");
        return builder.ToString().Trim();
    }

    private static string FormatCount(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private sealed record ShellArgs(string Command, string? WorkingDirectory = null, int? TimeoutSeconds = null);

    private const string ShellDescription = "Execute a non-interactive command in the selected workspace executor.";

    private const string ShellInstructions = """
        Use this low-priority tool only when the task requires command execution or higher-priority tools do not fit.

        Usage:
        - Good uses include build commands, tests, package manager commands, git commands, process/runtime checks, and other executor-specific operations.
        - Do not use shell for file discovery, content search, reading, or file edits when higher-priority tools can perform the task.
        - Keep commands non-interactive.
        - Use the workingDirectory parameter instead of changing directories inside the command when possible.
        - Quote paths that contain spaces.
        - Capture the command output and report relevant failures.
        """;
}

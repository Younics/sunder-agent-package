using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Shell;

public sealed class ShellToolSource(IPackageExtensionCatalog extensionCatalog)
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentPromptContextContributor, IAgentToolPresentationResolver
{
    private readonly IPackageExtensionInvocationCatalog? _invocationCatalog =
        extensionCatalog as IPackageExtensionInvocationCatalog;

    private static readonly AgentToolDescriptor Descriptor = new(
        "shell",
        "Shell Command",
        ShellDescription,
        IsReadOnly: false,
        ArgumentsJsonSchema: """
        {"type":"object","properties":{"command":{"type":"string"},"workingDirectory":{"type":"string"},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":86400}},"required":["command"],"additionalProperties":false}
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

    public string ContributorId => SourceId;

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!IsShellToolId(request.ToolId))
        {
            return null;
        }

        var parsed = TryParseShellArgs(request.ArgumentsJson, out var args, out var error);

        var command = parsed ? args.Command : null;
        return new AgentToolPresentation(
            HeaderText: request.ResultSummary ?? CompactCommand(command),
            DetailMarkdown: parsed
                ? BuildShellDetailMarkdown(args)
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error),
            OutputText: request.TextContent);
    }

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Descriptor]);
    }

    public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Workspace is null
            || request.ExecutionBinding is null
            || !request.AvailableTools.Any(tool => IsShellToolId(tool.ToolId)))
        {
            return null;
        }
        if (!TryAcquireTarget(request.ExecutionTargetReference, request.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(request.ExecutionTargetReference);
            return null;
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var shell = await target.GetShellAsync(
                    new AgentExecutionTargetContext(
                        request.Session.SessionId,
                        request.Profile?.ProfileId,
                        request.Workspace,
                        request.ExecutionBinding),
                    invocationToken);
                return new AgentPromptContextContribution(
                [
                    new AgentPromptContextBlock(
                        "Selected Executor Shell",
                        shell.Description,
                        Priority: 70,
                        SourceId: SourceId,
                        Provenance: AgentContextProvenance.Extension,
                        Trust: AgentContextTrust.Untrusted),
                ]);
            });
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

        if (context.ExecutionBinding is null
            || !TryAcquireTarget(context.ExecutionTargetReference, context.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(context.ExecutionTargetReference);
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "The selected workspace is not bound to an installed execution target.");
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var readiness = await target.GetReadinessAsync(
                    new AgentExecutionTargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding)
                    {
                        ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                    },
                    invocationToken);
                return readiness.Status == AgentExecutionTargetReadinessStatus.Ready && target.Descriptor.SupportsShell
                    ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Workspace shell is ready.")
                    : new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, readiness.Message);
            });
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

        if (context.ExecutionBinding is null
            || !TryAcquireTarget(context.ExecutionTargetReference, context.ExecutionBinding, out var targetLease))
        {
            ThrowIfExactTargetUnavailable(context.ExecutionTargetReference);
            return Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "shell-target-required");
        }

        if (!TryParseShellArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "shell-arguments-invalid");
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, invocationToken) =>
            {
                var result = await target.ExecuteShellAsync(
                    new AgentExecutionTargetContext(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding, context.AllowOutsideConfiguredScope)
                    {
                        ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                    },
                    new AgentShellCommandRequest(args.Command, args.WorkingDirectory, args.TimeoutSeconds),
                    invocationToken);

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
                    BackendId: $"{target.Descriptor.TargetKind}:{target.Descriptor.TargetId}")
                {
                    RequiresPromptContextRefresh = true,
                };
            });
    }

    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? command = null;
        if (TryParseShellArgs(request.ArgumentsJson, out var args, out _))
        {
            command = args.Command;
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

    private bool TryAcquireTarget(
        IPackageExtensionReference<IAgentExecutionTarget>? selectedReference,
        AgentWorkspaceBindingRecord binding,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out IPackageExtensionLease<IAgentExecutionTarget>? lease)
    {
        var reference = selectedReference ?? ResolveCompatibilityTargetReference(binding);
        if (reference is null || !reference.TryAcquire(out lease))
        {
            lease = null;
            return false;
        }
        if (lease.RetirementToken.IsCancellationRequested
            || !IsBindingMatch(lease.Contribution.Descriptor, binding))
        {
            lease.Dispose();
            lease = null;
            return false;
        }

        return true;
    }

    private IPackageExtensionReference<IAgentExecutionTarget>? ResolveCompatibilityTargetReference(
        AgentWorkspaceBindingRecord binding)
    {
        if (_invocationCatalog is not null)
        {
            foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.ExecutionTargets))
            {
                if (!reference.TryAcquire(out var lease))
                {
                    continue;
                }
                using (lease)
                {
                    if (!lease.RetirementToken.IsCancellationRequested
                        && IsBindingMatch(lease.Contribution.Descriptor, binding))
                    {
                        return reference;
                    }
                }
            }

            return null;
        }

        var target = extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .FirstOrDefault(candidate => IsBindingMatch(candidate.Descriptor, binding));
        return target is null ? null : new CompatibilityTargetReference(target);
    }

    private static bool IsBindingMatch(
        AgentExecutionTargetDescriptor descriptor,
        AgentWorkspaceBindingRecord binding)
        => string.Equals(descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<TResult> InvokeTargetAsync<TResult>(
        IPackageExtensionLease<IAgentExecutionTarget> lease,
        CancellationToken cancellationToken,
        Func<IAgentExecutionTarget, CancellationToken, ValueTask<TResult>> callback)
    {
        using (lease)
        {
            var packageId = lease.PackageId;
            var retirementToken = lease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                var result = await callback(lease.Contribution, invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"Execution-target package '{packageId}' became unavailable while the callback was running.");
                }
                return result;
            }
            catch (OperationCanceledException exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Execution-target package '{packageId}' became unavailable while the callback was running.",
                    exception);
            }
        }
    }

    private static void ThrowIfExactTargetUnavailable(
        IPackageExtensionReference<IAgentExecutionTarget>? selectedReference)
    {
        if (selectedReference is not null)
        {
            throw new InvalidOperationException("The selected execution-target package is unavailable.");
        }
    }

    private static bool IsShellToolId(string toolId)
        => string.Equals(toolId, Descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
           || (Descriptor.Aliases?.Any(alias => string.Equals(alias, toolId, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static AgentToolResult Error(string toolId, string message, string code)
        => new(
            toolId,
            message,
            Content: AgentToolPresentationMarkdown.BuildFailureMarkdown("Shell tool failed", message),
            IsError: true,
            ErrorCode: code);

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

    private static bool TryParseShellArgs(string argumentsJson, out ShellArgs args, out string? error)
    {
        args = new ShellArgs(string.Empty);
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("command", out var command, out error)
            || !arguments.TryReadOptionalString("workingDirectory", out var workingDirectory, out error)
            || !arguments.TryReadOptionalInt32("timeoutSeconds", out var timeoutSeconds, out error))
        {
            error = $"Invalid shell arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        if (!BoundedValue.IsInRange(timeoutSeconds, 1, BoundedProcessRunner.MaximumTimeoutSeconds))
        {
            error = $"Invalid shell arguments: timeoutSeconds must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds}.";
            return false;
        }

        args = new ShellArgs(command!, workingDirectory, timeoutSeconds);
        return true;
    }

    private static string BuildShellDetailMarkdown(ShellArgs args)
    {
        var builder = new StringBuilder();
        builder.AppendLine(AgentToolPresentationMarkdown.BuildFencedMarkdown("Command", "sh", args.Command));
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

    private static string FormatCount(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private sealed record ShellArgs(string Command, string? WorkingDirectory = null, int? TimeoutSeconds = null);

    private sealed class CompatibilityTargetReference(IAgentExecutionTarget target)
        : IPackageExtensionReference<IAgentExecutionTarget>
    {
        public bool TryAcquire(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out IPackageExtensionLease<IAgentExecutionTarget>? lease)
        {
            lease = new CompatibilityTargetLease(target);
            return true;
        }
    }

    private sealed class CompatibilityTargetLease(IAgentExecutionTarget target)
        : IPackageExtensionLease<IAgentExecutionTarget>
    {
        private IAgentExecutionTarget? _target = target;

        public string PackageId
        {
            get
            {
                ObjectDisposedException.ThrowIf(_target is null, this);
                return "sunder.package.agent.tools.shell.compatibility";
            }
        }

        public IAgentExecutionTarget Contribution
            => Volatile.Read(ref _target)
               ?? throw new ObjectDisposedException(nameof(CompatibilityTargetLease));

        public CancellationToken RetirementToken
        {
            get
            {
                ObjectDisposedException.ThrowIf(_target is null, this);
                return CancellationToken.None;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _target, null);
    }

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
        - Scoped AGENTS.md mutation preflight applies only to structured Files tools. Shell command paths cannot be inferred safely, so do not claim that shell mutations are instruction-enforced. Shell completion refreshes workspace-root and previously known scoped claims for the next provider cycle.
        """;
}

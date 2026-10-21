using System.Text;
using Sunder.Agent.Execution.Common;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
using IPermissionSurfaceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService;
using IPromptContextContributorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Prompt_Context_Contributor.IPromptContextContributorService;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Tools.Shell;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "shell.tools")]
[SunderServiceProvider(typeof(IPermissionSurfaceService), ProviderId = "shell.permissions")]
[SunderServiceProvider(typeof(IPromptContextContributorService), ProviderId = "shell.prompt.context")]
public sealed partial class ShellToolSource : IToolSourceService, IPermissionSurfaceService, IPromptContextContributorService
{
    private static readonly AgentToolDescriptor Descriptor = new()
    {
        ToolId = "shell", DisplayName = "Shell Command", Description = ShellDescription,
        IsReadOnly = false, RequiresNetwork = false,
        ArgumentsJsonSchema = """
        {"type":"object","properties":{"command":{"type":"string"},"workingDirectory":{"type":"string"},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":86400}},"required":["command"],"additionalProperties":false}
        """,
        SourceKind = "workspace", SourceId = "shell", SourceDisplayName = "Workspace Shell",
        Aliases = ["bash"], RuntimeInstructions = ShellInstructions, Priority = AgentToolPriority.Low,
        SelectionScope = AgentToolSelectionScope.Tool, SelectionGroupId = null, SelectionGroupDisplayName = null,
        SelectionGroupDescription = null, ActivationRequirement = null, ConcurrencyMode = AgentToolConcurrencyMode.Sequential,
    };

    public string SourceId => "workspace-shell";

    public string DisplayName => "Workspace Shell";

    public string SourceKind => "workspace";

    public string SurfaceId => "shell";

    public string ContributorId => SourceId;

    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = SourceId, DisplayName = DisplayName, SourceKind = SourceKind }); }

    ValueTask<AgentPermissionSurfaceDescriptor> IPermissionSurfaceService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentPermissionSurfaceDescriptor { SurfaceId = SurfaceId, DisplayName = DisplayName, Actions = ListActions() }); }

    ValueTask<AgentPromptContextContributorDescriptor> IPromptContextContributorService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentPromptContextContributorDescriptor { ContributorId = ContributorId, DisplayName = DisplayName, AcknowledgmentSinkId = null }); }

    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ResolveToolPresentation(request)); }

    private AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!IsShellToolId(request.ToolId))
        {
            return null;
        }

        var parsed = TryParseShellArgs(request.ArgumentsJson, out var args, out var error);

        var command = parsed ? args.Command : null;
        return new AgentToolPresentation
        {
            HeaderText = request.ResultSummary ?? CompactCommand(command),
            DetailMarkdown = parsed
                ? BuildShellDetailMarkdown(args)
                : AgentToolPresentationMarkdown.BuildRawRequestMarkdown(request.ArgumentsJson, error),
            OutputText = request.TextContent,
        };
    }

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Descriptor]);
    }

    public async ValueTask<AgentPromptContextContribution?> ContributeAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Workspace is null
            || request.ExecutionBinding is null
            || !request.AvailableTools.Any(tool => IsShellToolId(tool.ToolId)))
        {
            return null;
        }
        var targetLease = await AcquireTargetAsync(
                request.ExecutionTarget,
                request.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return null;
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, _, invocationToken) =>
            {
                var shell = await target.ShellAsync(
                    TargetContext(request.Session.SessionId, request.Profile?.ProfileId, request.Workspace, request.ExecutionBinding),
                    invocationToken);
                return new AgentPromptContextContribution
                {
                    Blocks = [new()
                    {
                        Title = "Selected Executor Shell", Content = shell.Description, Priority = 70, SourceId = SourceId,
                        Provenance = AgentContextProvenance.Extension, Trust = AgentContextTrust.Untrusted,
                        Usage = AgentPromptContextUsage.Reference, Authority = AgentPromptContextAuthority.Reference, Scope = null, HostIdentity = null,
                    }],
                };
            },
            static () => (AgentPromptContextContribution?)null);
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        var (toolId, context) = (request.ToolId, request.Context);
        if (!IsShellToolId(toolId))
        {
            return null;
        }

        if (context.Workspace is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "Shell tools require a selected workspace." };
        }

        if (context.ExecutionBinding is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "The selected workspace is not bound to an installed execution target." };
        }
        var targetLease = await AcquireTargetAsync(
                context.ExecutionTarget,
                context.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return new() { ToolId = toolId, Status = AgentToolReadinessStatus.Failed, Message = "The selected workspace is not bound to an installed execution target." };
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, descriptor, invocationToken) =>
            {
                var readiness = await target.ReadinessAsync(
                    TargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding) with
                    {
                        ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                    },
                    invocationToken);
                var ready = readiness.Status == AgentExecutionTargetReadinessStatus.Ready && descriptor.SupportsShell;
                return new AgentToolReadiness { ToolId = toolId, Status = ready ? AgentToolReadinessStatus.Ready : AgentToolReadinessStatus.Failed,
                    Message = ready ? "Workspace shell is ready." : readiness.Message };
            },
            () => new AgentToolReadiness { ToolId = toolId, Status = AgentToolReadinessStatus.Failed,
                Message = "The selected execution target became unavailable while readiness was being checked." });
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        if (context.Workspace is null)
        {
            return Error(request.ToolId, "Shell tools require a selected workspace.", "shell-workspace-required");
        }

        if (context.ExecutionBinding is null)
        {
            return Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "shell-target-required");
        }
        var targetLease = await AcquireTargetAsync(
                context.ExecutionTarget,
                context.ExecutionBinding,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "shell-target-required");
        }

        if (!TryParseShellArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "shell-arguments-invalid");
        }

        return await InvokeTargetAsync(
            targetLease,
            cancellationToken,
            async (target, descriptor, invocationToken) =>
            {
                var result = await target.ExecuteShellAsync(new()
                {
                    Context = TargetContext(context.SessionId, context.ProfileId, context.Workspace, context.ExecutionBinding) with
                    {
                        AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
                        ApprovedResourceReferences = context.ApprovedResourceReferences,
                        ApprovedResourceClaims = context.ApprovedResourceClaims,
                        ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
                        ResourceOperation = context.ResourceOperation,
                        ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
                    },
                    Request = new() { Command = args.Command, WorkingDirectory = args.WorkingDirectory, TimeoutSeconds = args.TimeoutSeconds },
                }, invocationToken);

                var content = string.IsNullOrWhiteSpace(result.Output)
                    ? $"Command exited with code {result.ExitCode} and no output."
                    : result.Output;
                return new AgentToolResult
                {
                    ToolId = request.ToolId, Summary = result.TimedOut ? "Shell command timed out" : $"Shell command exited with code {result.ExitCode}",
                    Content = content, WasTruncated = result.WasTruncated, IsError = result.ExitCode != 0,
                    ErrorCode = result.ExitCode == 0 ? null : result.TimedOut ? AgentToolResultErrorCodes.ShellTimeout : AgentToolResultErrorCodes.ShellNonZeroExit,
                    BackendId = $"{descriptor.TargetKind}:{descriptor.TargetId}", StructuredPayloadJson = null, Sources = null, PresentationPayloadJson = null,
                    RequiresPromptContextRefresh = true,
                };
            },
            () => Error(
                request.ToolId,
                "The selected execution target became unavailable while the command was running.",
                AgentToolResultErrorCodes.PackageUnavailable),
            preserveDispatchFailure: true);
    }

    public ValueTask<AgentPermissionRequest?> PermissionAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        cancellationToken.ThrowIfCancellationRequested();
        string? command = null;
        if (TryParseShellArgs(request.ArgumentsJson, out var args, out _))
        {
            command = args.Command;
        }

        return ValueTask.FromResult<AgentPermissionRequest?>(new AgentPermissionRequest
        {
            ActionId = "shell.execute", BoundaryId = "selected-execution-target",
            Summary = string.IsNullOrWhiteSpace(command) ? "Execute shell command" : command,
            ToolId = request.ToolId, Command = command, WorkspaceId = context.Workspace?.WorkspaceId,
            BindingId = context.ExecutionBinding?.BindingId, IsMutation = true, Path = null,
            ResourceDisplayName = null, ResourceReference = null, ResourceClaims = [], ResourceCapabilities = [],
            ResourceReferences = [], ScopeClassificationBasis = AgentPermissionScopeClassificationBasis.Unspecified,
        });
    }

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        =>
        [
            new()
            {
                ActionId = "shell.execute", DisplayName = "Execute shell commands", Description = "Run shell commands inside the selected workspace execution target.",
                Boundaries = [new()
                {
                    BoundaryId = "selected-execution-target", DisplayName = "Commands in selected workspace target",
                    Description = "Commands run by the selected execution target and working directory.", DefaultDecision = AgentPermissionDecision.Ask,
                }],
            },
        ];

    public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }

    public ValueTask AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    private static async ValueTask<ShellTarget?> AcquireTargetAsync(
        ServiceReference<IExecutionTargetService>? selectedReference,
        AgentWorkspaceBindingRecord binding,
        CancellationToken cancellationToken)
    {
        if (selectedReference is null)
        {
            return null;
        }

        var retirementToken = selectedReference.RetirementToken;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            retirementToken);
        try
        {
            await selectedReference.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var descriptor = await selectedReference.Service.DescribeAsync(invocation.Token).ConfigureAwait(false);
            if (retirementToken.IsCancellationRequested || !IsBindingMatch(descriptor, binding))
            {
                return null;
            }

            return new(selectedReference, descriptor);
        }
        catch (Exception exception) when (IsTargetUnavailable(
            exception,
            retirementToken,
            cancellationToken))
        {
            return null;
        }
    }

    private static bool IsBindingMatch(
        AgentExecutionTargetDescriptor descriptor,
        AgentWorkspaceBindingRecord binding)
        => string.Equals(descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<TResult> InvokeTargetAsync<TResult>(
        ShellTarget target,
        CancellationToken cancellationToken,
        Func<IExecutionTargetService, AgentExecutionTargetDescriptor, CancellationToken, ValueTask<TResult>> callback,
        Func<TResult> unavailableResult,
        bool preserveDispatchFailure = false)
    {
            var retirementToken = target.Reference.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            var callbackCompleted = false;
            try
            {
                var result = await callback(target.Reference.Service, target.Descriptor, invocation.Token).ConfigureAwait(false);
                callbackCompleted = true;
                cancellationToken.ThrowIfCancellationRequested();
                await target.Reference.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (preserveDispatchFailure)
                    {
                        retirementToken.ThrowIfCancellationRequested();
                    }
                    return unavailableResult();
                }
                return result;
            }
            catch (Exception exception) when (IsTargetUnavailable(
                exception,
                retirementToken,
                cancellationToken)
                && (!preserveDispatchFailure || !callbackCompleted && exception is SunderRpcException
                {
                    Error.DispatchState: SunderRpcDispatchState.NotDispatched,
                    Error.RetrySafety: SunderRpcRetrySafety.Safe,
                }))
            {
                // Mutating calls must preserve uncertain dispatch for Agent's ambiguity boundary.
                return unavailableResult();
            }
    }

    private sealed record ShellTarget(ServiceReference<IExecutionTargetService> Reference, AgentExecutionTargetDescriptor Descriptor);

    private static AgentExecutionTargetContext TargetContext(Guid? sessionId, string? profileId, AgentWorkspaceRecord workspace, AgentWorkspaceBindingRecord binding) => new()
    {
        SessionId = sessionId, ProfileId = profileId, Workspace = workspace, Binding = binding,
        AllowOutsideConfiguredScope = false, ApprovedResourceReferences = [], ApprovedResourceClaims = [], ApprovedResourceCapabilities = [],
        ResourceOperation = null, CapturePostMutationResource = false, ExpectedConfigurationGeneration = null,
    };

    private static bool IsTargetUnavailable(
        Exception exception,
        CancellationToken retirementToken,
        CancellationToken callerCancellationToken)
        => !callerCancellationToken.IsCancellationRequested
           && (exception is SunderRpcException
               {
                   Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable,
               }
               || retirementToken.IsCancellationRequested
               && (exception is OperationCanceledException
                   || exception is SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled }));

    private static bool IsShellToolId(string toolId)
        => string.Equals(toolId, Descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
           || (Descriptor.Aliases?.Any(alias => string.Equals(alias, toolId, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static AgentToolResult Error(string toolId, string message, string code)
        => new()
        {
            ToolId = toolId, Summary = message, Content = AgentToolPresentationMarkdown.BuildFailureMarkdown("Shell tool failed", message),
            IsError = true, ErrorCode = code, StructuredPayloadJson = null, Sources = null, WasTruncated = false,
            BackendId = null, PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
        };

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

        if (!BoundedValue.IsInRange(timeoutSeconds, 1, ProcessExecutionLimits.MaximumTimeoutSeconds))
        {
            error = $"Invalid shell arguments: timeoutSeconds must be between 1 and {ProcessExecutionLimits.MaximumTimeoutSeconds}.";
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

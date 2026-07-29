using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentPermissionStatusAdapter(IPackageExtensionCatalog extensionCatalog)
{
    private readonly IPackageExtensionInvocationCatalog _invocationCatalog =
        extensionCatalog as IPackageExtensionInvocationCatalog
        ?? throw new InvalidOperationException(
            "The host extension catalog does not support activation-scoped invocation leases.");

    public async ValueTask<bool> IsReadOnlySubagentAsync(
        SubagentRecord subagent,
        CancellationToken cancellationToken)
    {
        foreach (var assignment in subagent.SelectableCapabilityAssignments ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(assignment.Kind, AgentProfileSelectableCapabilityKinds.Tool, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var descriptor = await ResolveToolDescriptorAsync(assignment, cancellationToken);
            if (descriptor is null || !descriptor.IsReadOnly)
            {
                return false;
            }
        }

        return true;
    }

    public SubagentTaskResult AdaptChildResult(
        string resultToolId,
        AgentChildRunResult result,
        SubagentRecord subagent,
        string payloadJson)
    {
        var state = ToTaskState(result.Status);
        if (state == SubagentTaskResultState.Waiting)
        {
            return new SubagentTaskResult(
                state,
                CreateChildWaitCompatibilityResult(resultToolId, result, subagent, payloadJson));
        }

        if (state == SubagentTaskResultState.Completed)
        {
            var content = string.IsNullOrWhiteSpace(result.Content) ? result.Summary : result.Content!;
            return new SubagentTaskResult(
                state,
                new AgentToolResult(
                    resultToolId,
                    $"Subagent '{subagent.DisplayName}' completed.",
                    Content: SubagentBatchResultRenderer.TruncateParentResultContent(content),
                    StructuredPayloadJson: payloadJson,
                    BackendId: result.SessionId.ToString("N")));
        }

        return new SubagentTaskResult(
            state,
            new AgentToolResult(
                resultToolId,
                result.Summary,
                Content: SubagentBatchResultRenderer.TruncateParentResultContent(result.Content ?? result.Summary),
                StructuredPayloadJson: payloadJson,
                IsError: true,
                ErrorCode: AgentToolResultErrorCodes.SubagentRunFailed,
                BackendId: result.SessionId.ToString("N")));
    }

    public AgentToolResult CreateBatchResult(
        IReadOnlyList<SubagentTaskResult> results,
        string content,
        string payloadJson)
    {
        var waiting = results.FirstOrDefault(result => result.State == SubagentTaskResultState.Waiting);
        if (waiting is not null)
        {
            return CreateWaitCompatibilityResult(
                SubagentConstants.DelegateTasksToolId,
                "One or more delegated subagent tasks are waiting for approval.",
                content,
                payloadJson,
                waiting.ToolResult.BackendId);
        }

        var failed = results.Any(result => result.State != SubagentTaskResultState.Completed);
        return new AgentToolResult(
            SubagentConstants.DelegateTasksToolId,
            failed ? "One or more delegated subagent tasks failed." : "Delegated subagent tasks completed.",
            Content: content,
            StructuredPayloadJson: payloadJson,
            IsError: failed,
            ErrorCode: failed ? AgentToolResultErrorCodes.SubagentRunFailed : null);
    }

    public AgentToolResult CreateError(string toolId, string message, string code)
        => new(toolId, message, Content: $"### Task failed\n\n{message}", IsError: true, ErrorCode: code);

    public SubagentTaskResult CreateTaskFailure(string toolId, string message, string code)
        => new(SubagentTaskResultState.Failed, CreateError(toolId, message, code));

    public static SubagentTaskResultState ToTaskState(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Completed => SubagentTaskResultState.Completed,
            AgentRunStatus.WaitingForApproval => SubagentTaskResultState.Waiting,
            AgentRunStatus.Stopped => SubagentTaskResultState.Stopped,
            AgentRunStatus.Interrupted => SubagentTaskResultState.Interrupted,
            _ => SubagentTaskResultState.Failed,
        };

    private static AgentToolResult CreateChildWaitCompatibilityResult(
        string resultToolId,
        AgentChildRunResult result,
        SubagentRecord subagent,
        string payloadJson)
        => CreateWaitCompatibilityResult(
            resultToolId,
            $"Subagent '{subagent.DisplayName}' is waiting for approval.",
            $"Subagent '{subagent.DisplayName}' is waiting for user approval.",
            payloadJson,
            result.SessionId.ToString("N"));

    private static AgentToolResult CreateWaitCompatibilityResult(
        string toolId,
        string summary,
        string content,
        string payloadJson,
        string? backendId)
        => new(
            toolId,
            summary,
            Content: content,
            StructuredPayloadJson: payloadJson,
            IsError: false,
            ErrorCode: AgentToolResultErrorCodes.ChildWaitingForApproval,
            BackendId: backendId);

    private async ValueTask<AgentToolDescriptor?> ResolveToolDescriptorAsync(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        CancellationToken cancellationToken)
    {
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.Tools))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                var descriptor = lease.Contribution.Descriptor;
                if (!lease.RetirementToken.IsCancellationRequested
                    && IsToolAssignmentMatch(assignment, descriptor))
                {
                    return descriptor;
                }
            }
        }

        var context = new AgentToolSourceContext(SessionId: null, Profile: null, Workspace: null, ExecutionBinding: null);
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.ToolSources))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                var retirementToken = lease.RetirementToken;
                using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    retirementToken);
                IReadOnlyList<AgentToolDescriptor> descriptors;
                try
                {
                    descriptors = await lease.Contribution.ListToolsAsync(context, invocation.Token);
                }
                catch (OperationCanceledException) when (
                    retirementToken.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                foreach (var descriptor in descriptors)
                {
                    if (IsToolAssignmentMatch(assignment, descriptor))
                    {
                        return descriptor;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsToolAssignmentMatch(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        AgentToolDescriptor descriptor)
        => (string.Equals(assignment.CapabilityId, descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
            || (descriptor.Aliases?.Any(alias => string.Equals(assignment.CapabilityId, alias, StringComparison.OrdinalIgnoreCase)) ?? false))
           && (string.IsNullOrWhiteSpace(assignment.SourceId)
               || (!string.IsNullOrWhiteSpace(descriptor.SourceId)
                   && string.Equals(assignment.SourceId, descriptor.SourceId, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(descriptor.SourceKind)
                   && string.Equals(assignment.SourceId, descriptor.SourceKind, StringComparison.OrdinalIgnoreCase)));
}

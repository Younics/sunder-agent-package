using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Sdk.Services;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed partial class SubagentPermissionStatusAdapter(IServiceProviders<IToolSourceService> tools)
{
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
                Result(
                    resultToolId,
                    $"Subagent '{subagent.DisplayName}' completed.",
                    SubagentBatchResultRenderer.TruncateParentResultContent(content), payloadJson, result.SessionId.ToString("N")));
        }

        return new SubagentTaskResult(
            state,
            Result(
                resultToolId,
                result.Summary,
                SubagentBatchResultRenderer.TruncateParentResultContent(result.Content ?? result.Summary), payloadJson,
                result.SessionId.ToString("N"), true, AgentToolResultErrorCodes.SubagentRunFailed));
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
        return Result(
            SubagentConstants.DelegateTasksToolId,
            failed ? "One or more delegated subagent tasks failed." : "Delegated subagent tasks completed.",
            content, payloadJson, null, failed, failed ? AgentToolResultErrorCodes.SubagentRunFailed : null);
    }

    public AgentToolResult CreateError(string toolId, string message, string code)
        => Result(toolId, message, $"### Task failed\n\n{message}", null, null, true, code);

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
        => Result(toolId, summary, content, payloadJson, backendId, false, AgentToolResultErrorCodes.ChildWaitingForApproval);

    private async ValueTask<AgentToolDescriptor?> ResolveToolDescriptorAsync(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        CancellationToken cancellationToken)
    {
        var context = new AgentToolSourceContext { SessionId = null, Profile = null, Workspace = null, ExecutionBinding = null,
            ExecutionTarget = null, ExecutionTargetConfigurationGeneration = null };
        foreach (var provider in (await tools.ListAsync(cancellationToken).ConfigureAwait(false)).Providers.Where(item => item.State == ServiceProviderState.Active))
        {
            var result = await SubagentCatalogSelection.InvokeOptionalAsync(provider, cancellationToken,
                (service, token) => service.ListToolsAsync(context, token)).ConfigureAwait(false);
            if (!result.IsAvailable) continue;
            foreach (var descriptor in result.Value)
                if (IsToolAssignmentMatch(assignment, descriptor)) return descriptor;
        }

        return null;
    }

    private static AgentToolResult Result(string toolId, string summary, string? content, string? payload, string? backendId,
        bool isError = false, string? errorCode = null) => new()
    {
        ToolId = toolId, Summary = summary, Content = content, StructuredPayloadJson = payload, BackendId = backendId,
        IsError = isError, ErrorCode = errorCode, Sources = null, WasTruncated = false, PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
    };

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

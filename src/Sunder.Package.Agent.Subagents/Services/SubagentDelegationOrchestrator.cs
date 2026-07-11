using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentDelegationOrchestrator(
    SubagentRequestParser requestParser,
    SubagentDescriptorSchema descriptors,
    SubagentChildRunCoordinator childRunCoordinator,
    SubagentPermissionStatusAdapter permissionStatusAdapter,
    SubagentBatchResultRenderer resultRenderer)
{
    private readonly SubagentRequestParser _requestParser = requestParser;
    private readonly SubagentDescriptorSchema _descriptors = descriptors;
    private readonly SubagentChildRunCoordinator _childRunCoordinator = childRunCoordinator;
    private readonly SubagentPermissionStatusAdapter _permissionStatusAdapter = permissionStatusAdapter;
    private readonly SubagentBatchResultRenderer _resultRenderer = resultRenderer;

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(request.ToolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteBatchAsync(context, request.ArgumentsJson, cancellationToken);
        }

        if (!string.Equals(request.ToolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase))
        {
            return _permissionStatusAdapter.CreateError(
                request.ToolId,
                $"Subagent tool '{request.ToolId}' is not supported.",
                "subagent-tool-unsupported");
        }

        if (!_requestParser.TryParseTask(request.ArgumentsJson, out var task, out var error))
        {
            return _permissionStatusAdapter.CreateError(request.ToolId, error!, "task-arguments-invalid");
        }

        if (!_childRunCoordinator.TryPrepare(
                context,
                SubagentConstants.TaskToolId,
                out var environment,
                out var preparationFailure))
        {
            return preparationFailure!.ToolResult;
        }

        if (!TryResolveSubagent(environment!.ParentProfile, task, SubagentConstants.TaskToolId, out var subagent, out var resolutionFailure))
        {
            return resolutionFailure!.ToolResult;
        }

        return (await _childRunCoordinator.RunAsync(
            environment,
            SubagentConstants.TaskToolId,
            task,
            subagent!,
            cancellationToken)).ToolResult;
    }

    private async ValueTask<AgentToolResult> ExecuteBatchAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (!_requestParser.TryParseBatch(argumentsJson, out var batch, out var error))
        {
            return _permissionStatusAdapter.CreateError(
                SubagentConstants.DelegateTasksToolId,
                error!,
                "task-arguments-invalid");
        }

        if (!_childRunCoordinator.TryPrepare(
                context,
                SubagentConstants.DelegateTasksToolId,
                out var environment,
                out var preparationFailure))
        {
            return preparationFailure!.ToolResult;
        }

        var resolvedTasks = new List<(SubagentTaskRequest Request, SubagentRecord Subagent)>(batch.Tasks.Count);
        foreach (var task in batch.Tasks)
        {
            if (!TryResolveSubagent(
                    environment!.ParentProfile,
                    task,
                    SubagentConstants.DelegateTasksToolId,
                    out var subagent,
                    out var resolutionFailure))
            {
                return resolutionFailure!.ToolResult;
            }

            if (!await _permissionStatusAdapter.IsReadOnlySubagentAsync(subagent!, cancellationToken))
            {
                return _permissionStatusAdapter.CreateError(
                    SubagentConstants.DelegateTasksToolId,
                    $"Subagent '{subagent!.DisplayName}' is not read-only. Use the single task tool for subagents with mutating or unresolved capabilities.",
                    "subagent-batch-not-read-only");
            }

            resolvedTasks.Add((task, subagent!));
        }

        var executions = resolvedTasks
            .Select(task => _childRunCoordinator.RunAsync(
                environment!,
                SubagentConstants.DelegateTasksToolId,
                task.Request,
                task.Subagent,
                cancellationToken).AsTask())
            .ToArray();
        var results = await Task.WhenAll(executions);
        return _resultRenderer.BuildBatchResult(results);
    }

    private bool TryResolveSubagent(
        AgentProfileRecord parentProfile,
        SubagentTaskRequest request,
        string resultToolId,
        out SubagentRecord? subagent,
        out SubagentTaskResult? failure)
    {
        subagent = _descriptors.ResolveEnabledSubagent(parentProfile, request.SubagentType, requireUsable: false);
        if (subagent is null)
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(
                resultToolId,
                $"Subagent '{request.SubagentType}' is not enabled for this profile.",
                "subagent-not-enabled");
            return false;
        }

        if (!SubagentService.IsUsable(subagent))
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(
                resultToolId,
                $"Subagent '{subagent.DisplayName}' is missing a required description.",
                "subagent-description-required");
            return false;
        }

        failure = null;
        return true;
    }
}

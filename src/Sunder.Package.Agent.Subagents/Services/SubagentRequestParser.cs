using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentRequestParser
{
    public bool TryParseTask(string argumentsJson, out SubagentTaskRequest request, out string? error)
    {
        request = new SubagentTaskRequest();
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !TryReadTask(arguments!, out request, out error))
        {
            error = $"Invalid task arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        if (!ValidateTask(request, out error))
        {
            error = $"Invalid task arguments: {error}";
            return false;
        }

        return true;
    }

    public bool TryParseBatch(string argumentsJson, out SubagentBatchRequest request, out string? error)
    {
        request = new SubagentBatchRequest([]);
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadObjectArray("tasks", allowSingleObject: true, out var taskElements, out error))
        {
            error = $"Invalid delegate_tasks arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        if (taskElements.Count == 0)
        {
            error = "Invalid delegate_tasks arguments: at least one task is required.";
            return false;
        }

        if (taskElements.Count > SubagentConstants.MaxBatchDelegationCount)
        {
            error = $"Delegate tasks supports at most {SubagentConstants.MaxBatchDelegationCount} tasks.";
            return false;
        }

        var tasks = new List<SubagentTaskRequest>(taskElements.Count);
        foreach (var taskElement in taskElements)
        {
            if (!AgentToolArgumentObject.TryParse(taskElement.GetRawText(), out var taskArguments, out error)
                || !TryReadTask(taskArguments!, out var task, out error)
                || !ValidateTask(task, out error))
            {
                error = $"Invalid delegate_tasks arguments: {error ?? "task arguments were empty or invalid."}";
                return false;
            }

            tasks.Add(task);
        }

        var taskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in tasks.Select(task => Normalize(task.TaskId)).Where(taskId => taskId is not null))
        {
            if (!taskIds.Add(taskId!))
            {
                error = $"Invalid delegate_tasks arguments: duplicate task_id '{taskId}' is not allowed within a batch.";
                return false;
            }
        }

        request = new SubagentBatchRequest(tasks);
        error = null;
        return true;
    }

    private static bool TryReadTask(
        AgentToolArgumentObject arguments,
        out SubagentTaskRequest request,
        out string? error)
    {
        request = new SubagentTaskRequest();
        if (!arguments.TryReadOptionalString("description", out var description, out error)
            || !arguments.TryReadOptionalString("prompt", out var prompt, out error)
            || !arguments.TryReadOptionalString("subagent_type", out var subagentType, out error)
            || !arguments.TryReadOptionalString("task_id", out var taskId, out error)
            || !arguments.TryReadOptionalString("command", out var command, out error))
        {
            return false;
        }

        request = new SubagentTaskRequest(description, prompt, subagentType, taskId, command);
        return true;
    }

    private static bool ValidateTask(SubagentTaskRequest request, out string? error)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || string.IsNullOrWhiteSpace(request.SubagentType))
        {
            error = "each task requires prompt and subagent_type.";
            return false;
        }

        error = null;
        return true;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record SubagentTaskRequest(
    string? Description = null,
    string? Prompt = null,
    string? SubagentType = null,
    string? TaskId = null,
    string? Command = null);

internal sealed record SubagentBatchRequest(IReadOnlyList<SubagentTaskRequest> Tasks);

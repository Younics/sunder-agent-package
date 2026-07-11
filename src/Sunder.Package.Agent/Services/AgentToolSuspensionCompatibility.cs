using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

internal static class AgentToolSuspensionCompatibility
{
    public static bool TryCreateChildJoin(
        AgentToolCallRequest toolCall,
        AgentToolResult toolResult,
        Guid userTurnId,
        out AgentChildJoinRunSuspension? suspension)
    {
        suspension = null;
        if (!string.Equals(
                toolResult.ErrorCode,
                AgentToolResultErrorCodes.ChildWaitingForApproval,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var outstanding = new List<AgentChildJoinTask>();
        var completed = new List<AgentChildJoinTaskResult>();
        if (!string.IsNullOrWhiteSpace(toolResult.StructuredPayloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(toolResult.StructuredPayloadJson);
                if (document.RootElement.TryGetProperty("tasks", out var tasks)
                    && tasks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var task in tasks.EnumerateArray())
                    {
                        AddTask(task, toolCall.CallId, outstanding, completed);
                    }
                }
                else
                {
                    AddTask(document.RootElement, toolCall.CallId, outstanding, completed);
                }
            }
            catch (JsonException)
            {
                // Fall back to the legacy BackendId adapter below.
            }
        }

        if (outstanding.Count == 0
            && Guid.TryParse(toolResult.BackendId, out var legacyChildSessionId))
        {
            outstanding.Add(new AgentChildJoinTask(legacyChildSessionId, toolCall.CallId));
        }

        if (outstanding.Count == 0)
        {
            return false;
        }

        suspension = new AgentChildJoinRunSuspension(
            userTurnId,
            toolCall.ToolId,
            toolCall.ArgumentsJson,
            outstanding,
            completed);
        return true;
    }

    private static void AddTask(
        JsonElement task,
        string toolCallId,
        ICollection<AgentChildJoinTask> outstanding,
        ICollection<AgentChildJoinTaskResult> completed)
    {
        if (!task.TryGetProperty("childSessionId", out var sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(sessionIdElement.GetString(), out var childSessionId))
        {
            return;
        }

        var state = GetString(task, "state");
        var title = GetString(task, "childSessionTitle");
        if (string.Equals(state, "waiting", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(state))
        {
            outstanding.Add(new AgentChildJoinTask(childSessionId, toolCallId, title));
            return;
        }

        completed.Add(new AgentChildJoinTaskResult(
            childSessionId,
            toolCallId,
            ParseStatus(state),
            GetString(task, "resultSummary") ?? state,
            GetString(task, "resultContent"),
            title));
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static AgentRunStatus ParseStatus(string state)
        => state.ToLowerInvariant() switch
        {
            "completed" => AgentRunStatus.Completed,
            "stopped" => AgentRunStatus.Stopped,
            "interrupted" => AgentRunStatus.Interrupted,
            _ => AgentRunStatus.Failed,
        };
}

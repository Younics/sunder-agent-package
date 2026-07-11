using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentBatchResultRenderer(
    SubagentService subagentService,
    SubagentRequestParser requestParser,
    SubagentPermissionStatusAdapter permissionStatusAdapter)
{
    private const int MaxSingleParentResultContentLength = 24_000;
    private const int MaxDelegatedParentResultContentLength = 32_000;
    private const int MaxStructuredResultSummaryLength = 2_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SubagentService _subagentService = subagentService;
    private readonly SubagentRequestParser _requestParser = requestParser;
    private readonly SubagentPermissionStatusAdapter _permissionStatusAdapter = permissionStatusAdapter;

    public AgentToolResult BuildBatchResult(IReadOnlyList<SubagentTaskResult> results)
    {
        var content = BuildDelegateTasksResultContent(results);
        var payload = JsonSerializer.Serialize(new DelegateTasksPayload
        {
            Tasks = results
                .Select(result => SanitizeParentPayload(ParseTaskPayload(result.ToolResult.StructuredPayloadJson)))
                .ToArray(),
        }, JsonOptions);
        return _permissionStatusAdapter.CreateBatchResult(results, content, payload);
    }

    public string BuildChildSessionPayload(
        AgentChildRunResult result,
        SubagentRecord subagent,
        string fallbackTitle,
        SubagentTaskResultState state)
        => JsonSerializer.Serialize(new TaskPayload
        {
            ChildSessionId = result.SessionId,
            ChildSessionTitle = FirstNonBlank(result.Title, fallbackTitle),
            SubagentId = subagent.SubagentId,
            SubagentName = subagent.DisplayName,
            State = state.ToString().ToLowerInvariant(),
            ResultSummary = TruncateStructuredSummary(result.Summary),
        }, JsonOptions);

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (string.Equals(request.ToolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase))
        {
            var parsed = _requestParser.TryParseBatch(request.ArgumentsJson, out var batch, out var error);
            return new AgentToolPresentation(
                HeaderText: parsed ? $"Delegated {batch.Tasks.Count} task(s)" : request.ResultSummary,
                DetailMarkdown: parsed ? BuildDelegateTasksDetailMarkdown(batch) : BuildRawArgumentsMarkdown(request.ArgumentsJson, error),
                OutputText: request.TextContent?.Trim());
        }

        if (!string.Equals(request.ToolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsedTask = _requestParser.TryParseTask(request.ArgumentsJson, out var task, out var taskError);
        var payload = ParseTaskPayload(request.StructuredPayloadJson);
        var argumentSubagentName = parsedTask ? ResolveSubagentDisplayName(task.SubagentType) : null;
        var subagentName = FirstNonBlank(payload.SubagentName, argumentSubagentName, task.SubagentType, "subagent")!;
        var sessionTitle = FirstNonBlank(payload.ChildSessionTitle, task.Description, "Subagent session")!;
        return new AgentToolPresentation(
            HeaderText: $"{FormatSubagentLabel(subagentName)} · {sessionTitle}",
            DetailMarkdown: parsedTask
                ? BuildTaskDetailMarkdown(task, payload, argumentSubagentName)
                : BuildRawArgumentsMarkdown(request.ArgumentsJson, taskError),
            OutputText: request.TextContent?.Trim());
    }

    internal static string BuildDelegateTasksResultContent(IReadOnlyList<SubagentTaskResult> results)
    {
        var builder = new StringBuilder();
        foreach (var taskResult in results)
        {
            if (builder.Length >= MaxDelegatedParentResultContentLength)
            {
                AppendParentResultTruncationNotice(builder);
                break;
            }

            var result = taskResult.ToolResult;
            var payload = ParseTaskPayload(result.StructuredPayloadJson);
            var subagentName = FirstNonBlank(payload.SubagentName, "subagent")!;
            var taskId = payload.ChildSessionId?.ToString("N") ?? result.BackendId ?? "unknown";
            builder.Append("<task_result subagent=\"")
                .Append(EscapeXmlAttribute(subagentName))
                .Append("\" task_id=\"")
                .Append(EscapeXmlAttribute(taskId))
                .Append("\" status=\"")
                .Append(taskResult.State.ToString().ToLowerInvariant())
                .AppendLine("\">");
            var content = string.IsNullOrWhiteSpace(result.Content) ? result.Summary : result.Content!.Trim();
            builder.AppendLine(TruncateParentResultContent(content));
            builder.AppendLine("</task_result>");
            builder.AppendLine();

            if (builder.Length > MaxDelegatedParentResultContentLength)
            {
                builder.Length = MaxDelegatedParentResultContentLength;
                AppendParentResultTruncationNotice(builder);
                break;
            }
        }

        return builder.ToString().Trim();
    }

    internal static string TruncateParentResultContent(string content)
    {
        if (content.Length <= MaxSingleParentResultContentLength)
        {
            return content;
        }

        return content[..MaxSingleParentResultContentLength].TrimEnd()
               + "\n\n[Subagent output truncated in the parent transcript. Open the sub-session for the full result.]";
    }

    private string? ResolveSubagentDisplayName(string? subagentType)
    {
        if (string.IsNullOrWhiteSpace(subagentType))
        {
            return null;
        }

        var normalized = subagentType.Trim();
        return _subagentService.ListSubagents()
            .FirstOrDefault(agent => string.Equals(agent.SubagentId, normalized, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(agent.DisplayName, normalized, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName;
    }

    private static TaskPayload ParseTaskPayload(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson))
        {
            return new TaskPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<TaskPayload>(structuredPayloadJson, JsonOptions) ?? new TaskPayload();
        }
        catch (JsonException)
        {
            return new TaskPayload();
        }
    }

    private static TaskPayload SanitizeParentPayload(TaskPayload payload)
        => payload with
        {
            ResultSummary = TruncateStructuredSummary(payload.ResultSummary),
            ResultContent = null,
        };

    private static string? TruncateStructuredSummary(string? summary)
        => string.IsNullOrWhiteSpace(summary)
            ? null
            : summary.Length <= MaxStructuredResultSummaryLength
                ? summary
                : summary[..MaxStructuredResultSummaryLength].TrimEnd() + " [truncated; open the child session for the full result]";

    private static string BuildTaskDetailMarkdown(
        SubagentTaskRequest task,
        TaskPayload payload,
        string? argumentSubagentName)
    {
        var lines = new List<string>();
        var subagentName = FirstNonBlank(payload.SubagentName, argumentSubagentName, task.SubagentType);
        var sessionTitle = FirstNonBlank(payload.ChildSessionTitle, task.Description);
        if (!string.IsNullOrWhiteSpace(subagentName) || !string.IsNullOrWhiteSpace(sessionTitle))
        {
            lines.Add("**Subagent Session**");
            if (!string.IsNullOrWhiteSpace(subagentName))
            {
                lines.Add($"- Subagent: {FormatSubagentLabel(subagentName)}");
            }

            if (!string.IsNullOrWhiteSpace(sessionTitle))
            {
                lines.Add($"- Session: {sessionTitle}");
            }
        }

        if (!string.IsNullOrWhiteSpace(task.Command))
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("**Command**");
            lines.Add(task.Command.Trim());
        }

        return string.Join("\n", lines).Trim();
    }

    private static string BuildDelegateTasksDetailMarkdown(SubagentBatchRequest batch)
    {
        var lines = new List<string> { "**Delegated Tasks**" };
        foreach (var task in batch.Tasks)
        {
            var subagentType = FirstNonBlank(task.SubagentType, "subagent")!;
            var description = FirstNonBlank(task.Description, "Task")!;
            lines.Add($"- {description}: {FormatSubagentLabel(subagentType)}");
        }

        return string.Join("\n", lines).Trim();
    }

    private static string BuildRawArgumentsMarkdown(string argumentsJson, string? error)
    {
        var builder = new StringBuilder();
        builder.AppendLine("**Arguments**");
        if (!string.IsNullOrWhiteSpace(error))
        {
            builder.AppendLine(error.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("```json");
        builder.AppendLine(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim());
        builder.AppendLine("```");
        return builder.ToString().Trim();
    }

    private static void AppendParentResultTruncationNotice(StringBuilder builder)
        => builder.AppendLine()
            .AppendLine("[Additional delegated subagent output truncated in the parent transcript. Open the sub-sessions for full results.]");

    private static string EscapeXmlAttribute(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FormatSubagentLabel(string subagentName)
        => subagentName.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? subagentName
            : $"{subagentName} subagent";

    private sealed class DelegateTasksPayload
    {
        public IReadOnlyList<TaskPayload>? Tasks { get; init; }
    }

    private sealed record TaskPayload
    {
        public Guid? ChildSessionId { get; init; }

        public string? ChildSessionTitle { get; init; }

        public string? SubagentId { get; init; }

        public string? SubagentName { get; init; }

        public string? State { get; init; }

        public string? ResultSummary { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResultContent { get; init; }
    }
}

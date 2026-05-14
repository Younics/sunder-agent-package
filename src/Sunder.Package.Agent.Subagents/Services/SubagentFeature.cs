using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentFeature(
    SubagentService subagentService,
    IPackageExtensionCatalog extensionCatalog) : IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier, IAgentToolSource, IAgentSystemPromptContributor, IAgentToolPresentationResolver
{
    private const int MaxBatchDelegationCount = 3;
    private const int MaxSingleParentResultContentLength = 24_000;
    private const int MaxDelegatedParentResultContentLength = 32_000;
    private const string SourceDisplayName = "Subagents";

    private readonly SubagentService _subagentService = subagentService;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public string ProviderId => SubagentConstants.PackageId;

    public string SourceId => SubagentConstants.PackageId;

    public string SourceKind => "subagent";

    public string DisplayName => SourceDisplayName;

    public string ContributorId => SubagentConstants.PackageId;

    public event Action? SelectableCapabilitiesChanged
    {
        add => _subagentService.SubagentsChanged += value;
        remove => _subagentService.SubagentsChanged -= value;
    }

    public async ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        return _subagentService.ListSubagents()
            .Select(agent => new AgentProfileSelectableCapabilityDescriptor(
                AgentProfileSelectableCapabilityKinds.Subagent,
                agent.SubagentId,
                SubagentConstants.PackageId,
                agent.DisplayName,
                agent.Description,
                SubagentService.IsUsable(agent)
                    ? "Available as a delegated task subagent."
                    : "Description is required before this subagent can be selected or used.",
                SubagentService.IsUsable(agent),
                SourceDisplayName: SourceDisplayName,
                GroupId: SubagentConstants.PackageId,
                GroupDisplayName: SourceDisplayName,
                GroupDescription: "Delegated specialists available to orchestrated profiles.",
                GroupSortOrder: 40))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledSubagents = ListEnabledSubagents(context.Profile, requireUsable: true);
        if (!IsOrchestratedProfile(context.Profile) || enabledSubagents.Count == 0)
        {
            return [];
        }

        var descriptors = new List<AgentToolDescriptor>
        {
            new(
                SubagentConstants.TaskToolId,
                "Task",
                BuildTaskToolDescription(enabledSubagents),
                IsReadOnly: false,
                ArgumentsJsonSchema: BuildTaskArgumentsSchema(enabledSubagents),
                SourceKind: SourceKind,
                SourceId: SourceId,
                SourceDisplayName: SourceDisplayName,
                RuntimeInstructions: BuildRuntimeInstructions(enabledSubagents),
                ActivationRequirement: new AgentToolActivationRequirement(AgentProfileSelectableCapabilityKinds.Subagent, SubagentConstants.PackageId),
                Priority: AgentToolPriority.High),
        };

        var readOnlySubagents = new List<SubagentRecord>();
        foreach (var subagent in enabledSubagents)
        {
            if (await IsReadOnlySubagentAsync(subagent, cancellationToken))
            {
                readOnlySubagents.Add(subagent);
            }
        }

        if (readOnlySubagents.Count > 0)
        {
            descriptors.Add(new AgentToolDescriptor(
                SubagentConstants.DelegateTasksToolId,
                "Delegate Tasks",
                BuildDelegateTasksToolDescription(readOnlySubagents),
                IsReadOnly: true,
                ArgumentsJsonSchema: BuildDelegateTasksArgumentsSchema(readOnlySubagents),
                SourceKind: SourceKind,
                SourceId: SourceId,
                SourceDisplayName: SourceDisplayName,
                RuntimeInstructions: BuildRuntimeInstructions(enabledSubagents),
                ActivationRequirement: new AgentToolActivationRequirement(AgentProfileSelectableCapabilityKinds.Subagent, SubagentConstants.PackageId),
                Priority: AgentToolPriority.High));
        }

        return descriptors;
    }

    public ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string requestedToolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasEnabledSubagents = IsOrchestratedProfile(context.Profile)
                                  && ListEnabledSubagents(context.Profile, requireUsable: true).Count > 0;
        if (!hasEnabledSubagents)
        {
            return ValueTask.FromResult<AgentToolReadiness?>(null);
        }

        return ValueTask.FromResult<AgentToolReadiness?>(
            string.Equals(requestedToolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedToolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase)
                ? new AgentToolReadiness(requestedToolId, AgentToolReadinessStatus.Ready, "Ready.")
                : null);
    }

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (string.Equals(request.ToolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase))
        {
            var parsed = TryParseDelegateTasksArgs(request.ArgumentsJson, out var batchArgs, out var error);
            return new AgentToolPresentation(
                HeaderText: parsed ? $"Delegated {batchArgs.Tasks?.Count ?? 0} task(s)" : request.ResultSummary,
                DetailMarkdown: parsed ? BuildDelegateTasksDetailMarkdown(batchArgs) : BuildRawArgumentsMarkdown(request.ArgumentsJson, error),
                OutputText: request.TextContent?.Trim());
        }

        if (!string.Equals(request.ToolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsedTaskArgs = TryParseTaskArgs(request.ArgumentsJson, out var args, out var taskError);
        var payload = ParseTaskPayload(request.StructuredPayloadJson);
        var argumentSubagentName = parsedTaskArgs ? ResolveSubagentDisplayName(args.SubagentType) : null;
        var subagentName = FirstNonBlank(payload.SubagentName, argumentSubagentName, args.SubagentType, "subagent")!;
        var sessionTitle = FirstNonBlank(payload.ChildSessionTitle, args.Description, "Subagent session")!;
        var header = $"{FormatSubagentLabel(subagentName)} · {sessionTitle}";
        return new AgentToolPresentation(
            HeaderText: header,
            DetailMarkdown: parsedTaskArgs ? BuildTaskDetailMarkdown(args, payload, argumentSubagentName) : BuildRawArgumentsMarkdown(request.ArgumentsJson, taskError),
            OutputText: request.TextContent?.Trim());
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(request.ToolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteDelegateTasksAsync(context, request, cancellationToken);
        }

        if (!string.Equals(request.ToolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase))
        {
            return Error(request.ToolId, $"Subagent tool '{request.ToolId}' is not supported.", "subagent-tool-unsupported");
        }

        if (!TryParseTaskArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "task-arguments-invalid");
        }

        return await ExecuteSingleTaskAsync(context, SubagentConstants.TaskToolId, args, cancellationToken);
    }

    public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
        AgentSystemPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledSubagents = ListEnabledSubagents(request.Profile, requireUsable: true);
        if (!IsOrchestratedProfile(request.Profile) || enabledSubagents.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<AgentSystemPromptBlock>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<AgentSystemPromptBlock>>(
        [
            new AgentSystemPromptBlock(
                "subagent-task-guidance",
                "Subagent Delegation",
                BuildRuntimeInstructions(enabledSubagents),
                Priority: 120,
                Required: true,
                SourceId: SubagentConstants.PackageId)
        ]);
    }

    private async ValueTask<AgentToolResult> ExecuteDelegateTasksAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (context.SessionId is null || context.RunId is null || context.RunRevision is null || string.IsNullOrWhiteSpace(context.ToolCallId))
        {
            return Error(SubagentConstants.DelegateTasksToolId, "Delegate tasks requires an active parent run context.", "task-context-missing");
        }

        var runtimeCatalog = _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
        if (runtimeCatalog is null)
        {
            return Error(SubagentConstants.DelegateTasksToolId, "Delegate tasks requires the base Agent runtime catalog extension.", "task-runtime-unavailable");
        }

        var parentProfile = string.IsNullOrWhiteSpace(context.ProfileId) ? null : runtimeCatalog.GetProfile(context.ProfileId);
        if (!IsOrchestratedProfile(parentProfile))
        {
            return Error(SubagentConstants.DelegateTasksToolId, "Delegate tasks is only available to profiles using orchestrated behavior.", "task-loop-disabled");
        }

        if (!TryParseDelegateTasksArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(SubagentConstants.DelegateTasksToolId, error!, "task-arguments-invalid");
        }

        if (args.Tasks is not { Count: > 0 })
        {
            return Error(SubagentConstants.DelegateTasksToolId, "Delegate tasks requires at least one task.", "task-arguments-invalid");
        }

        if (args.Tasks.Count > MaxBatchDelegationCount)
        {
            return Error(SubagentConstants.DelegateTasksToolId, $"Delegate tasks supports at most {MaxBatchDelegationCount} tasks.", "task-count-exceeded");
        }

        foreach (var task in args.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Prompt) || string.IsNullOrWhiteSpace(task.SubagentType))
            {
                return Error(SubagentConstants.DelegateTasksToolId, "Each delegated task requires prompt and subagent_type.", "task-arguments-invalid");
            }

            var subagent = ResolveEnabledSubagent(parentProfile, task.SubagentType, requireUsable: false);
            if (subagent is null)
            {
                return Error(SubagentConstants.DelegateTasksToolId, $"Subagent '{task.SubagentType}' is not enabled for this profile.", "subagent-not-enabled");
            }

            if (!SubagentService.IsUsable(subagent))
            {
                return Error(SubagentConstants.DelegateTasksToolId, $"Subagent '{subagent.DisplayName}' is missing a required description.", "subagent-description-required");
            }

            if (!await IsReadOnlySubagentAsync(subagent, cancellationToken))
            {
                return Error(SubagentConstants.DelegateTasksToolId, $"Subagent '{subagent.DisplayName}' is not read-only. Use the single task tool for subagents with mutating or unresolved capabilities.", "subagent-batch-not-read-only");
            }
        }

        var executions = args.Tasks
            .Select(task => ExecuteSingleTaskAsync(context, SubagentConstants.DelegateTasksToolId, task, cancellationToken).AsTask())
            .ToArray();
        var results = await Task.WhenAll(executions);
        var content = BuildDelegateTasksResultContent(results);
        var payload = BuildDelegateTasksPayload(results);
        var waitingResult = results.FirstOrDefault(result => string.Equals(result.ErrorCode, AgentToolResultErrorCodes.ChildWaitingForApproval, StringComparison.OrdinalIgnoreCase));
        if (waitingResult is not null)
        {
            return new AgentToolResult(
                SubagentConstants.DelegateTasksToolId,
                "One or more delegated subagent tasks are waiting for approval.",
                Content: content,
                StructuredPayloadJson: payload,
                IsError: false,
                ErrorCode: AgentToolResultErrorCodes.ChildWaitingForApproval,
                BackendId: waitingResult.BackendId);
        }

        var failed = results.Any(result => result.IsError);
        return new AgentToolResult(
            SubagentConstants.DelegateTasksToolId,
            failed ? "One or more delegated subagent tasks failed." : "Delegated subagent tasks completed.",
            Content: content,
            StructuredPayloadJson: payload,
            IsError: failed,
            ErrorCode: failed ? AgentToolResultErrorCodes.SubagentRunFailed : null);
    }

    private async ValueTask<AgentToolResult> ExecuteSingleTaskAsync(
        AgentToolExecutionContext context,
        string resultToolId,
        TaskArgs args,
        CancellationToken cancellationToken)
    {
        if (context.SessionId is null || context.RunId is null || context.RunRevision is null || string.IsNullOrWhiteSpace(context.ToolCallId))
        {
            return Error(resultToolId, "Task tool requires an active parent run context.", "task-context-missing");
        }

        var runtimeCatalog = _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
        if (runtimeCatalog is null)
        {
            return Error(resultToolId, "Task tool requires the base Agent runtime catalog extension.", "task-runtime-unavailable");
        }

        var childRunExecutor = _extensionCatalog.GetExtensions(PackageExtensionPoints.ChildRunExecutors).FirstOrDefault();
        if (childRunExecutor is null)
        {
            return Error(resultToolId, "Task tool requires the base Agent child-run executor extension.", "task-child-executor-unavailable");
        }

        if (string.IsNullOrWhiteSpace(context.Workspace?.WorkspaceId))
        {
            return Error(resultToolId, "Task tool requires an active workspace.", "task-workspace-unavailable");
        }

        var parentProfile = string.IsNullOrWhiteSpace(context.ProfileId) ? null : runtimeCatalog.GetProfile(context.ProfileId);
        if (!IsOrchestratedProfile(parentProfile))
        {
            return Error(resultToolId, "The task tool is only available to profiles using orchestrated behavior.", "task-loop-disabled");
        }

        if (string.IsNullOrWhiteSpace(args.Prompt) || string.IsNullOrWhiteSpace(args.SubagentType))
        {
            return Error(resultToolId, "Task requires prompt and subagent_type.", "task-arguments-invalid");
        }

        var subagent = ResolveEnabledSubagent(parentProfile, args.SubagentType, requireUsable: false);
        if (subagent is null)
        {
            return Error(resultToolId, $"Subagent '{args.SubagentType}' is not enabled for this profile.", "subagent-not-enabled");
        }

        if (!SubagentService.IsUsable(subagent))
        {
            return Error(resultToolId, $"Subagent '{subagent.DisplayName}' is missing a required description.", "subagent-description-required");
        }

        var childProfile = BuildChildProfile(parentProfile!, subagent);
        var childSessionTitle = string.IsNullOrWhiteSpace(args.Description) ? subagent.DisplayName : args.Description!.Trim();
        var result = await childRunExecutor.RunChildAsync(
            new AgentChildRunRequest(
                context.SessionId.Value,
                context.RunId.Value,
                context.RunRevision.Value,
                context.ToolCallId,
                context.Workspace.WorkspaceId,
                args.TaskId,
                childProfile,
                args.Prompt!,
                childSessionTitle),
            cancellationToken);
        var childSessionPayload = BuildChildSessionPayload(result, subagent, childSessionTitle);

        if (result.Status == AgentRunStatus.WaitingForApproval)
        {
            return new AgentToolResult(
                resultToolId,
                $"Subagent '{subagent.DisplayName}' is waiting for approval.",
                Content: $"Subagent '{subagent.DisplayName}' is waiting for user approval.",
                StructuredPayloadJson: childSessionPayload,
                IsError: false,
                ErrorCode: AgentToolResultErrorCodes.ChildWaitingForApproval,
                BackendId: result.SessionId.ToString("N"));
        }

        if (result.Status != AgentRunStatus.Completed)
        {
            return new AgentToolResult(
                resultToolId,
                result.Summary,
                Content: TruncateParentResultContent(result.Content ?? result.Summary, MaxSingleParentResultContentLength),
                StructuredPayloadJson: childSessionPayload,
                IsError: true,
                ErrorCode: AgentToolResultErrorCodes.SubagentRunFailed,
                BackendId: result.SessionId.ToString("N"));
        }

        var content = string.IsNullOrWhiteSpace(result.Content) ? result.Summary : result.Content!;
        return new AgentToolResult(
            resultToolId,
            $"Subagent '{subagent.DisplayName}' completed.",
            Content: TruncateParentResultContent(content, MaxSingleParentResultContentLength),
            StructuredPayloadJson: childSessionPayload,
            BackendId: result.SessionId.ToString("N"));
    }

    private IReadOnlyList<SubagentRecord> ListEnabledSubagents(AgentProfileRecord? profile, bool requireUsable)
    {
        if (profile?.SelectableCapabilityAssignments is null)
        {
            return [];
        }

        var enabledIds = profile.SelectableCapabilityAssignments
            .Where(assignment => string.Equals(assignment.Kind, AgentProfileSelectableCapabilityKinds.Subagent, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(assignment.SourceId, SubagentConstants.PackageId, StringComparison.OrdinalIgnoreCase))
            .Select(assignment => assignment.CapabilityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _subagentService.ListSubagents()
            .Where(agent => enabledIds.Contains(agent.SubagentId)
                            && (!requireUsable || SubagentService.IsUsable(agent)))
            .ToArray();
    }

    private SubagentRecord? ResolveEnabledSubagent(AgentProfileRecord? profile, string subagentType, bool requireUsable)
        => ListEnabledSubagents(profile, requireUsable)
            .FirstOrDefault(agent => string.Equals(agent.SubagentId, subagentType, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(agent.DisplayName, subagentType, StringComparison.OrdinalIgnoreCase));

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

    private static bool IsOrchestratedProfile(AgentProfileRecord? profile)
        => profile is not null
           && string.Equals(profile.BehaviorLoopId, SubagentConstants.OrchestratedBehaviorLoopId, StringComparison.OrdinalIgnoreCase);

    private static string BuildTaskToolDescription(IReadOnlyList<SubagentRecord> agents)
        => string.Join("\n", [
            "Launch a specialized subagent for a focused delegated task and return its final result.",
            "Use this tool when the user's request matches one or more enabled subagent descriptions, or when delegating an independent unit of work would improve quality, speed, or context management.",
            "Prefer delegation for specialized work that a listed subagent is explicitly described to handle.",
            "Do not use this tool when no enabled subagent description matches the task, when the task is trivial, or when direct use of a simple tool is enough.",
            "",
            "Enabled subagents:",
            FormatSubagentList(agents)
        ]);

    private static string BuildDelegateTasksToolDescription(IReadOnlyList<SubagentRecord> agents)
        => string.Join("\n", [
            $"Launch 1-{MaxBatchDelegationCount} independent read-only subagent tasks concurrently and return their final results.",
            "Use this when multiple enabled read-only subagent descriptions match independent parts of the user's request.",
            "Do not use this for mutating work, dependent tasks, trivial one-step work, or work that no listed subagent is explicitly described to handle.",
            "",
            "Enabled read-only subagents:",
            FormatSubagentList(agents)
        ]);

    private static string BuildRuntimeInstructions(IReadOnlyList<SubagentRecord> agents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are using an orchestrated profile with delegated subagents.");
        builder.AppendLine("Before doing substantial work yourself, compare the user request against the enabled subagent descriptions.");
        builder.AppendLine("If one or more descriptions clearly match independent parts of the request, call the task tool for those parts.");
        builder.AppendLine("Use the parent agent for coordination, synthesis, final user communication, and direct work that does not match any subagent.");
        builder.AppendLine("Do not invent subagent purposes. Only delegate based on the descriptions listed here.");
        builder.AppendLine("When multiple independent matching read-only subagent tasks are needed and the delegate_tasks tool is available, delegate them together. Otherwise delegate the highest-value matching task first.");
        builder.AppendLine();
        builder.AppendLine("Enabled subagents:");
        builder.AppendLine(FormatSubagentList(agents));
        return builder.ToString().Trim();
    }

    private static string FormatSubagentList(IReadOnlyList<SubagentRecord> agents)
        => string.Join("\n", agents.Select(agent => $"- {agent.DisplayName} (`{agent.SubagentId}`): {agent.Description!.Trim()}"));

    private static string BuildTaskArgumentsSchema(IReadOnlyList<SubagentRecord> agents)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["description"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Short task description." },
                ["prompt"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Detailed instructions for the subagent." },
                ["subagent_type"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Enabled subagent id or display name. Use one of: " + FormatSubagentTypeOptions(agents) },
                ["task_id"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional previous task id." },
                ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional command that triggered the delegation." },
            },
            ["required"] = new[] { "description", "prompt", "subagent_type" },
            ["additionalProperties"] = false,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string BuildDelegateTasksArgumentsSchema(IReadOnlyList<SubagentRecord> agents)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["tasks"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = MaxBatchDelegationCount,
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["description"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Short task description." },
                            ["prompt"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Detailed instructions for the subagent." },
                            ["subagent_type"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Enabled read-only subagent id or display name. Use one of: " + FormatSubagentTypeOptions(agents) },
                            ["task_id"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional previous task id." },
                        },
                        ["required"] = new[] { "description", "prompt", "subagent_type" },
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new[] { "tasks" },
            ["additionalProperties"] = false,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string FormatSubagentTypeOptions(IReadOnlyList<SubagentRecord> agents)
        => string.Join(", ", agents.Select(agent => $"{agent.DisplayName} (`{agent.SubagentId}`)"));

    private async ValueTask<bool> IsReadOnlySubagentAsync(SubagentRecord subagent, CancellationToken cancellationToken)
    {
        var assignments = subagent.SelectableCapabilityAssignments ?? [];
        foreach (var assignment in assignments)
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

    private async ValueTask<AgentToolDescriptor?> ResolveToolDescriptorAsync(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        CancellationToken cancellationToken)
    {
        foreach (var tool in _extensionCatalog.GetExtensions(PackageExtensionPoints.Tools))
        {
            if (IsToolAssignmentMatch(assignment, tool.Descriptor))
            {
                return tool.Descriptor;
            }
        }

        var context = new AgentToolSourceContext(SessionId: null, Profile: null, Workspace: null, ExecutionBinding: null);
        foreach (var source in _extensionCatalog.GetExtensions(PackageExtensionPoints.ToolSources))
        {
            foreach (var descriptor in await source.ListToolsAsync(context, cancellationToken))
            {
                if (IsToolAssignmentMatch(assignment, descriptor))
                {
                    return descriptor;
                }
            }
        }

        return null;
    }

    private static bool IsToolAssignmentMatch(AgentProfileSelectableCapabilityAssignmentRecord assignment, AgentToolDescriptor descriptor)
        => (string.Equals(assignment.CapabilityId, descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
            || (descriptor.Aliases?.Any(alias => string.Equals(assignment.CapabilityId, alias, StringComparison.OrdinalIgnoreCase)) ?? false))
           && (string.IsNullOrWhiteSpace(assignment.SourceId)
               || (!string.IsNullOrWhiteSpace(descriptor.SourceId)
                   && string.Equals(assignment.SourceId, descriptor.SourceId, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(descriptor.SourceKind)
                   && string.Equals(assignment.SourceId, descriptor.SourceKind, StringComparison.OrdinalIgnoreCase)));

    private static AgentProfileRecord BuildChildProfile(AgentProfileRecord parentProfile, SubagentRecord subagent)
    {
        var now = DateTimeOffset.UtcNow;
        var profileId = $"subagent-{subagent.SubagentId}";
        var parentChatBinding = FindModelBinding(parentProfile, AgentModelCapabilityKinds.Chat);
        var hasChatOverride = !string.IsNullOrWhiteSpace(subagent.ChatProviderId);
        var chatProviderId = hasChatOverride ? subagent.ChatProviderId : parentChatBinding?.ProviderId ?? parentProfile.ChatProviderId;
        var chatModelId = hasChatOverride ? subagent.ChatModelId : parentChatBinding?.ModelId ?? parentProfile.ChatModelId;
        var chatSettingsJson = hasChatOverride ? subagent.ChatModelSettingsJson : parentChatBinding?.SettingsJson;
        var embeddingBinding = FindModelBinding(parentProfile, AgentModelCapabilityKinds.Embedding);
        var bindings = new List<AgentProfileModelBindingRecord>();
        if (!string.IsNullOrWhiteSpace(chatProviderId) || !string.IsNullOrWhiteSpace(chatModelId))
        {
            bindings.Add(new AgentProfileModelBindingRecord(profileId, AgentModelCapabilityKinds.Chat, chatProviderId, chatModelId, chatSettingsJson, now));
        }

        if (embeddingBinding is not null)
        {
            bindings.Add(embeddingBinding with { ProfileId = profileId, UpdatedAtUtc = now });
        }

        return new AgentProfileRecord(
            profileId,
            subagent.DisplayName,
            subagent.Description,
            subagent.Instructions,
            chatProviderId,
            chatModelId,
            embeddingBinding?.ProviderId ?? parentProfile.EmbeddingProviderId,
            embeddingBinding?.ModelId ?? parentProfile.EmbeddingModelId,
            now,
            now,
            bindings,
            subagent.SelectableCapabilityAssignments ?? [],
            "default",
            null,
            null,
            IsInternal: true);
    }

    private static AgentProfileModelBindingRecord? FindModelBinding(AgentProfileRecord profile, string capabilityKind)
        => profile.ModelBindings?.FirstOrDefault(binding => string.Equals(binding.CapabilityKind, capabilityKind, StringComparison.OrdinalIgnoreCase));

    private static AgentToolResult Error(string toolId, string message, string code)
        => new(toolId, message, Content: $"### Task failed\n\n{message}", IsError: true, ErrorCode: code);

    private static string BuildChildSessionPayload(AgentChildRunResult result, SubagentRecord subagent, string fallbackTitle)
        => JsonSerializer.Serialize(new TaskPayload
        {
            ChildSessionId = result.SessionId,
            ChildSessionTitle = FirstNonBlank(result.Title, fallbackTitle),
            SubagentId = subagent.SubagentId,
            SubagentName = subagent.DisplayName,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string BuildDelegateTasksPayload(IReadOnlyList<AgentToolResult> results)
        => JsonSerializer.Serialize(new DelegateTasksPayload
        {
            Tasks = results.Select(result => ParseTaskPayload(result.StructuredPayloadJson)).ToArray(),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string BuildDelegateTasksResultContent(IReadOnlyList<AgentToolResult> results)
    {
        var builder = new StringBuilder();
        foreach (var result in results)
        {
            if (builder.Length >= MaxDelegatedParentResultContentLength)
            {
                AppendParentResultTruncationNotice(builder);
                break;
            }

            var payload = ParseTaskPayload(result.StructuredPayloadJson);
            var subagentName = FirstNonBlank(payload.SubagentName, "subagent")!;
            var taskId = payload.ChildSessionId?.ToString("N") ?? result.BackendId ?? "unknown";
            builder.Append("<task_result subagent=\"")
                .Append(EscapeXmlAttribute(subagentName))
                .Append("\" task_id=\"")
                .Append(EscapeXmlAttribute(taskId))
                .Append("\" status=\"")
                .Append(result.IsError ? "failed" : "completed")
                .AppendLine("\">");
            var content = string.IsNullOrWhiteSpace(result.Content) ? result.Summary : result.Content!.Trim();
            builder.AppendLine(TruncateParentResultContent(content, MaxSingleParentResultContentLength));
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

    private static string TruncateParentResultContent(string content, int maxLength)
    {
        if (content.Length <= maxLength)
        {
            return content;
        }

        return content[..maxLength].TrimEnd()
               + "\n\n[Subagent output truncated in the parent transcript. Open the sub-session for the full result.]";
    }

    private static void AppendParentResultTruncationNotice(StringBuilder builder)
    {
        builder.AppendLine()
            .AppendLine("[Additional delegated subagent output truncated in the parent transcript. Open the sub-sessions for full results.]");
    }

    private static string EscapeXmlAttribute(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static TaskPayload ParseTaskPayload(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson))
        {
            return new TaskPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<TaskPayload>(structuredPayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new TaskPayload();
        }
        catch
        {
            return new TaskPayload();
        }
    }

    private static string BuildTaskDetailMarkdown(TaskArgs args, TaskPayload payload, string? argumentSubagentName)
    {
        var lines = new List<string>();
        var subagentName = FirstNonBlank(payload.SubagentName, argumentSubagentName, args.SubagentType);
        var sessionTitle = FirstNonBlank(payload.ChildSessionTitle, args.Description);
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

        if (!string.IsNullOrWhiteSpace(args.Command))
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("**Command**");
            lines.Add(args.Command.Trim());
        }

        return string.Join("\n", lines).Trim();
    }

    private static string BuildDelegateTasksDetailMarkdown(DelegateTasksArgs args)
    {
        if (args.Tasks is not { Count: > 0 })
        {
            return string.Empty;
        }

        var lines = new List<string> { "**Delegated Tasks**" };
        foreach (var task in args.Tasks)
        {
            var subagentType = FirstNonBlank(task.SubagentType, "subagent")!;
            var description = FirstNonBlank(task.Description, "Task")!;
            lines.Add($"- {description}: {FormatSubagentLabel(subagentType)}");
        }

        return string.Join("\n", lines).Trim();
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FormatSubagentLabel(string subagentName)
        => subagentName.EndsWith("subagent", StringComparison.OrdinalIgnoreCase)
            ? subagentName
            : $"{subagentName} subagent";

    private static bool TryParseTaskArgs(string argumentsJson, out TaskArgs args, out string? error)
    {
        args = new TaskArgs();
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !TryReadTaskArgs(arguments!, out args, out error))
        {
            error = $"Invalid task arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        return true;
    }

    private static bool TryParseDelegateTasksArgs(string argumentsJson, out DelegateTasksArgs args, out string? error)
    {
        args = new DelegateTasksArgs();
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadObjectArray("tasks", allowSingleObject: true, out var taskElements, out error))
        {
            error = $"Invalid delegate_tasks arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        var tasks = new List<TaskArgs>();
        foreach (var taskElement in taskElements)
        {
            var taskArguments = AgentToolArgumentObject.TryParse(taskElement.GetRawText(), out var parsedTask, out error)
                ? parsedTask!
                : null;
            if (taskArguments is null || !TryReadTaskArgs(taskArguments, out var taskArgs, out error))
            {
                error = $"Invalid delegate_tasks arguments: {error ?? "task arguments were empty or invalid."}";
                return false;
            }

            tasks.Add(taskArgs);
        }

        args = new DelegateTasksArgs { Tasks = tasks };
        return true;
    }

    private static bool TryReadTaskArgs(AgentToolArgumentObject arguments, out TaskArgs args, out string? error)
    {
        args = new TaskArgs();
        if (!arguments.TryReadOptionalString("description", out var description, out error)
            || !arguments.TryReadOptionalString("prompt", out var prompt, out error)
            || !arguments.TryReadOptionalString("subagent_type", out var subagentType, out error)
            || !arguments.TryReadOptionalString("task_id", out var taskId, out error)
            || !arguments.TryReadOptionalString("command", out var command, out error))
        {
            return false;
        }

        args = new TaskArgs
        {
            Description = description,
            Prompt = prompt,
            SubagentType = subagentType,
            TaskId = taskId,
            Command = command,
        };
        return true;
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

    private sealed class DelegateTasksArgs
    {
        public IReadOnlyList<TaskArgs>? Tasks { get; set; }
    }

    private sealed class TaskArgs
    {
        public string? Description { get; set; }

        public string? Prompt { get; set; }

        [JsonPropertyName("subagent_type")]
        public string? SubagentType { get; set; }

        [JsonPropertyName("task_id")]
        public string? TaskId { get; set; }

        public string? Command { get; set; }
    }

    private sealed class DelegateTasksPayload
    {
        public IReadOnlyList<TaskPayload>? Tasks { get; set; }
    }

    private sealed class TaskPayload
    {
        public Guid? ChildSessionId { get; set; }

        public string? ChildSessionTitle { get; set; }

        public string? SubagentId { get; set; }

        public string? SubagentName { get; set; }
    }
}

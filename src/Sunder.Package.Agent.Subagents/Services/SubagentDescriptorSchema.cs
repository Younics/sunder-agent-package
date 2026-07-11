using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentDescriptorSchema(
    SubagentService subagentService,
    IPackageExtensionCatalog extensionCatalog,
    SubagentPermissionStatusAdapter permissionStatusAdapter)
{
    internal const string SourceDisplayName = "Subagents";

    private readonly SubagentService _subagentService = subagentService;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly SubagentPermissionStatusAdapter _permissionStatusAdapter = permissionStatusAdapter;

    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsSubagentFeature(request.Profile))
        {
            return ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>(
            _subagentService.ListSubagents()
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
                    GroupDescription: "Delegated specialists available to behavior loops with subagent support.",
                    GroupSortOrder: 40))
                .ToArray());
    }

    public async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledSubagents = ListEnabledSubagents(context.Profile, requireUsable: true);
        if (!SupportsSubagentFeature(context.Profile) || enabledSubagents.Count == 0)
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
                SourceKind: "subagent",
                SourceId: SubagentConstants.PackageId,
                SourceDisplayName: SourceDisplayName,
                RuntimeInstructions: BuildRuntimeInstructions(enabledSubagents),
                ActivationRequirement: new AgentToolActivationRequirement(AgentProfileSelectableCapabilityKinds.Subagent, SubagentConstants.PackageId),
                Priority: AgentToolPriority.High),
        };

        var readOnlySubagents = new List<SubagentRecord>();
        foreach (var subagent in enabledSubagents)
        {
            if (await _permissionStatusAdapter.IsReadOnlySubagentAsync(subagent, cancellationToken))
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
                SourceKind: "subagent",
                SourceId: SubagentConstants.PackageId,
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasEnabledSubagents = SupportsSubagentFeature(context.Profile)
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

    public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
        AgentSystemPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledSubagents = ListEnabledSubagents(request.Profile, requireUsable: true);
        if (!SupportsSubagentFeature(request.Profile) || enabledSubagents.Count == 0)
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
                SourceId: SubagentConstants.PackageId),
        ]);
    }

    public IReadOnlyList<SubagentRecord> ListEnabledSubagents(AgentProfileRecord? profile, bool requireUsable)
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

    public SubagentRecord? ResolveEnabledSubagent(AgentProfileRecord? profile, string? subagentType, bool requireUsable)
        => string.IsNullOrWhiteSpace(subagentType)
            ? null
            : ListEnabledSubagents(profile, requireUsable)
                .FirstOrDefault(agent => string.Equals(agent.SubagentId, subagentType, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(agent.DisplayName, subagentType, StringComparison.OrdinalIgnoreCase));

    public bool SupportsSubagentFeature(AgentProfileRecord? profile)
    {
        if (profile is null)
        {
            return false;
        }

        var requestedLoopId = string.IsNullOrWhiteSpace(profile.BehaviorLoopId)
            ? AgentBehaviorLoopIds.Default
            : profile.BehaviorLoopId.Trim();
        var requestedSourceId = string.IsNullOrWhiteSpace(profile.BehaviorLoopSourceId)
            ? null
            : profile.BehaviorLoopSourceId.Trim();
        var loops = _extensionCatalog.GetExtensions(PackageExtensionPoints.BehaviorLoops);
        var behaviorLoop = loops.FirstOrDefault(loop =>
                               string.Equals(loop.Descriptor.LoopId, requestedLoopId, StringComparison.OrdinalIgnoreCase)
                               && (requestedSourceId is null
                                   || string.Equals(loop.Descriptor.SourceId, requestedSourceId, StringComparison.OrdinalIgnoreCase)))
                           ?? loops.FirstOrDefault(loop =>
                               string.Equals(loop.Descriptor.LoopId, AgentBehaviorLoopIds.Default, StringComparison.OrdinalIgnoreCase));
        return behaviorLoop?.Descriptor.FeatureKinds?.Any(kind =>
            string.Equals(kind, SubagentConstants.FeatureKind, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string BuildTaskToolDescription(IReadOnlyList<SubagentRecord> agents)
        => string.Join("\n", [
            "Launch a specialized subagent for a focused delegated task and return its final result.",
            "Use this tool when the user's request matches one or more enabled subagent descriptions, or when delegating an independent unit of work would improve quality, speed, or context management.",
            "Prefer delegation for specialized work that a listed subagent is explicitly described to handle.",
            "Do not use this tool when no enabled subagent description matches the task, when the task is trivial, or when direct use of a simple tool is enough.",
            "",
            "Enabled subagents:",
            FormatSubagentList(agents),
        ]);

    private static string BuildDelegateTasksToolDescription(IReadOnlyList<SubagentRecord> agents)
        => string.Join("\n", [
            $"Launch 1-{SubagentConstants.MaxBatchDelegationCount} independent read-only subagent tasks concurrently and return their final results.",
            "Use this when multiple enabled read-only subagent descriptions match independent parts of the user's request.",
            "Do not use this for mutating work, dependent tasks, trivial one-step work, or work that no listed subagent is explicitly described to handle.",
            "",
            "Enabled read-only subagents:",
            FormatSubagentList(agents),
        ]);

    private static string BuildRuntimeInstructions(IReadOnlyList<SubagentRecord> agents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are using a profile with delegated subagents.");
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
            ["properties"] = TaskProperties(agents),
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
                    ["maxItems"] = SubagentConstants.MaxBatchDelegationCount,
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = TaskProperties(agents),
                        ["required"] = new[] { "description", "prompt", "subagent_type" },
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new[] { "tasks" },
            ["additionalProperties"] = false,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static Dictionary<string, object?> TaskProperties(IReadOnlyList<SubagentRecord> agents)
        => new()
        {
            ["description"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Short task description." },
            ["prompt"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Detailed instructions for the subagent." },
            ["subagent_type"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Enabled subagent id or display name. Use one of: " + FormatSubagentTypeOptions(agents) },
            ["task_id"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional previous task id." },
            ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Optional command that triggered the delegation." },
        };

    private static string FormatSubagentTypeOptions(IReadOnlyList<SubagentRecord> agents)
        => string.Join(", ", agents.Select(agent => $"{agent.DisplayName} (`{agent.SubagentId}`)"));
}

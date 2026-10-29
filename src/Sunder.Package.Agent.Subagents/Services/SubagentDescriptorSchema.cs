using System.Text;
using System.Text.Json;
using IBehaviorLoopService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Behavior_Loop.IBehaviorLoopService;
using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Sunder.Sdk.Services;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed partial class SubagentDescriptorSchema(
    SubagentService subagentService,
    SubagentPermissionStatusAdapter permissionStatusAdapter,
    IServiceProviders<IRuntimeCatalogService> catalogs,
    IServiceProviders<IBehaviorLoopService> behaviorLoops)
{
    internal const string SourceDisplayName = "Subagents";

    private readonly SubagentService _subagentService = subagentService;
    private readonly SubagentPermissionStatusAdapter _permissionStatusAdapter = permissionStatusAdapter;
    public async ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileRecord? profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await SupportsSubagentFeatureAsync(profile, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return _subagentService.ListSubagents()
                .Select(agent => new AgentProfileSelectableCapabilityDescriptor
                {
                    Kind = AgentProfileSelectableCapabilityKinds.Subagent, CapabilityId = agent.SubagentId,
                    SourceId = SubagentConstants.PackageId, DisplayName = agent.DisplayName, Description = agent.Description,
                    StatusText = SubagentService.IsUsable(agent)
                        ? null
                        : "Add a description to enable this subagent.",
                    IsSelectable = SubagentService.IsUsable(agent), SourceDisplayName = SourceDisplayName,
                    GroupId = SubagentConstants.PackageId, GroupDisplayName = SourceDisplayName,
                    GroupDescription = "Subagents available for delegated tasks.", GroupSortOrder = 40, GroupAllowsBulkSelection = false,
                })
                .ToArray();
    }

    public async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabledSubagents = ListEnabledSubagents(context.Profile, requireUsable: true);
        if (!await SupportsSubagentFeatureAsync(context.Profile, cancellationToken).ConfigureAwait(false) || enabledSubagents.Count == 0)
        {
            return [];
        }

        var descriptors = new List<AgentToolDescriptor>
        {
            TaskDescriptor(SubagentConstants.TaskToolId, "Task", BuildTaskToolDescription(enabledSubagents), false, BuildTaskArgumentsSchema(enabledSubagents)),
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
            descriptors.Add(TaskDescriptor(SubagentConstants.DelegateTasksToolId, "Delegate Tasks", BuildDelegateTasksToolDescription(readOnlySubagents),
                true, BuildDelegateTasksArgumentsSchema(readOnlySubagents)));
        }

        return descriptors;
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string requestedToolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasEnabledSubagents = await SupportsSubagentFeatureAsync(context.Profile, cancellationToken).ConfigureAwait(false)
                                   && ListEnabledSubagents(context.Profile, requireUsable: true).Count > 0;
        if (!hasEnabledSubagents)
        {
            return null;
        }

        return string.Equals(requestedToolId, SubagentConstants.TaskToolId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedToolId, SubagentConstants.DelegateTasksToolId, StringComparison.OrdinalIgnoreCase)
                ? new AgentToolReadiness { ToolId = requestedToolId, Status = AgentToolReadinessStatus.Ready, Message = "Ready." }
                : null;
    }

    public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = request.Profile
                       ?? await ResolveProfileAsync(request.Session.ProfileId, cancellationToken).ConfigureAwait(false);
        var enabledSubagents = ListEnabledSubagents(profile, requireUsable: true);
        if (!await SupportsSubagentFeatureAsync(profile, cancellationToken).ConfigureAwait(false) || enabledSubagents.Count == 0)
        {
            return null;
        }

        return new AgentPromptContextContribution
        {
            Blocks = [new()
            {
                Title = "Subagent Delegation", Content = "Enabled subagents:\n" + FormatSubagentList(enabledSubagents), Priority = 120,
                SourceId = SubagentConstants.PackageId, Provenance = AgentContextProvenance.User, Trust = AgentContextTrust.UserProvided,
                Usage = AgentPromptContextUsage.Reference, Scope = null, Authority = AgentPromptContextAuthority.Reference, HostIdentity = null,
            }],
        };
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

    public async ValueTask<bool> SupportsSubagentFeatureAsync(AgentProfileRecord? profile, CancellationToken token)
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
        var loops = (await SubagentCatalogSelection.SnapshotAsync(behaviorLoops, token,
            static (service, cancellation) => service.DescribeAsync(cancellation)).ConfigureAwait(false)).Select(item => item.Metadata).ToArray();
        var behaviorLoop = loops.FirstOrDefault(loop =>
                               string.Equals(loop.LoopId, requestedLoopId, StringComparison.OrdinalIgnoreCase)
                               && (requestedSourceId is null
                                   || string.Equals(loop.SourceId, requestedSourceId, StringComparison.OrdinalIgnoreCase)))
                           ?? loops.FirstOrDefault(loop =>
                               string.Equals(loop.LoopId, AgentBehaviorLoopIds.Default, StringComparison.OrdinalIgnoreCase));
        return behaviorLoop?.FeatureKinds?.Any(kind =>
            string.Equals(kind, SubagentConstants.FeatureKind, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async ValueTask<AgentProfileRecord?> ResolveProfileAsync(string profileId, CancellationToken token)
    {
        foreach (var provider in (await catalogs.ListAsync(token).ConfigureAwait(false)).Providers.Where(item => item.State == ServiceProviderState.Active))
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, provider.RetirementToken);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var profile = await provider.Service.GetProfileAsync(profileId, invocation.Token).ConfigureAwait(false);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return profile;
        }

        return null;
    }

    private static AgentToolDescriptor TaskDescriptor(string id, string displayName, string description, bool readOnly, string schema) => new()
    {
        ToolId = id, DisplayName = displayName, Description = description, IsReadOnly = readOnly, RequiresNetwork = false,
        ArgumentsJsonSchema = schema, SourceKind = "subagent", SourceId = SubagentConstants.PackageId, SourceDisplayName = SourceDisplayName,
        RuntimeInstructions = BuildRuntimeInstructions(), Priority = AgentToolPriority.High,
        ActivationRequirement = new() { CapabilityKind = AgentProfileSelectableCapabilityKinds.Subagent, SourceId = SubagentConstants.PackageId, CapabilityId = null },
        Aliases = null, SelectionScope = AgentToolSelectionScope.Tool, SelectionGroupId = null, SelectionGroupDisplayName = null,
        SelectionGroupDescription = null, SelectionGroupAllowsBulkSelection = true, ConcurrencyMode = AgentToolConcurrencyMode.Sequential,
    };

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

    private static string BuildRuntimeInstructions()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are using a profile with delegated subagents.");
        builder.AppendLine("Before doing substantial work yourself, compare the user request against the enabled subagent descriptions.");
        builder.AppendLine("If one or more descriptions clearly match independent parts of the request, call the task tool for those parts.");
        builder.AppendLine("Use the parent agent for coordination, synthesis, final user communication, and direct work that does not match any subagent.");
        builder.AppendLine("Do not invent subagent purposes. Only delegate based on the descriptions listed here.");
        builder.AppendLine("When multiple independent matching read-only subagent tasks are needed and the delegate_tasks tool is available, delegate them together. Otherwise delegate the highest-value matching task first.");
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

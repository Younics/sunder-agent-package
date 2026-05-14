using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentToolService(
    InstalledPackageToolSource installedPackageToolSource,
    AgentSessionService sessionService,
    AgentWorkspaceService workspaceService,
    AgentExecutionTargetService executionTargetService,
    IPackageExtensionCatalog extensionCatalog)
{
    private readonly InstalledPackageToolSource _installedPackageToolSource = installedPackageToolSource;
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentExecutionTargetService _executionTargetService = executionTargetService;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListToolCatalogAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        bool includeUnavailable = true,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        var context = new AgentToolSourceContext(sessionId, effectiveProfile, workspace, ResolveExecutionBinding(workspace));
        var catalog = new List<AgentToolCatalogEntry>();
        foreach (var source in GetSources())
        {
            var descriptors = await source.ListToolsAsync(context, cancellationToken);
            foreach (var descriptor in descriptors.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var readiness = await source.GetReadinessAsync(descriptor.ToolId, context, cancellationToken)
                    ?? new AgentToolReadiness(descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready.");

                if (!includeUnavailable && readiness.Status != AgentToolReadinessStatus.Ready)
                {
                    continue;
                }

                catalog.Add(new AgentToolCatalogEntry(descriptor, readiness));
            }
        }

        return catalog
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default)
    {
        var context = new AgentToolSourceContext(SessionId: null, Profile: null, Workspace: null, ExecutionBinding: null);
        var catalog = new List<AgentToolCatalogEntry>();
        foreach (var source in GetSources())
        {
            var descriptors = await source.ListToolsAsync(context, cancellationToken);
            catalog.AddRange(descriptors
                .Where(descriptor => descriptor.SelectionScope == AgentToolSelectionScope.Tool)
                .Select(descriptor => new AgentToolCatalogEntry(
                descriptor,
                new AgentToolReadiness(
                    descriptor.ToolId,
                    AgentToolReadinessStatus.Ready,
                    "Installed capability. Runtime readiness depends on the selected session workspace."))));
        }

        return catalog
            .GroupBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentToolReadiness>> ListToolReadinessAsync(CancellationToken cancellationToken = default)
        => (await ListToolCatalogAsync(includeUnavailable: true, cancellationToken: cancellationToken))
            .Select(item => item.Readiness)
            .ToArray();

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListReadyToolDescriptorsAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        return (await ListToolCatalogAsync(profile, sessionId, workspace, includeUnavailable: false, cancellationToken))
            .Where(item => IsAllowedForProfile(effectiveProfile, item.Descriptor))
            .Select(item => item.Descriptor)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentRuntimeTool>> ListReadyRuntimeToolsAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        var context = new AgentToolSourceContext(sessionId, effectiveProfile, workspace, ResolveExecutionBinding(workspace));
        var tools = new List<AgentRuntimeTool>();
        foreach (var source in GetSources())
        {
            var runtimeTools = await ListRuntimeToolsAsync(source, context, cancellationToken);
            foreach (var runtimeTool in runtimeTools.OrderBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var readiness = await source.GetReadinessAsync(runtimeTool.Descriptor.ToolId, context, cancellationToken)
                    ?? new AgentToolReadiness(runtimeTool.Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready.");
                if (readiness.Status != AgentToolReadinessStatus.Ready || !IsAllowedForProfile(effectiveProfile, runtimeTool.Descriptor))
                {
                    continue;
                }

                tools.Add(runtimeTool);
            }
        }

        return tools
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolId,
        string argumentsJson,
        Guid? sessionId = null,
        string? profileId = null,
        AgentWorkspaceRecord? workspace = null,
        bool allowOutsideConfiguredScope = false,
        Guid? runId = null,
        long? runRevision = null,
        Guid? userTurnId = null,
        string? toolCallId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var context = new AgentToolExecutionContext(sessionId, effectiveProfileId, workspace, ResolveExecutionBinding(workspace), allowOutsideConfiguredScope, runId, runRevision, userTurnId, toolCallId);
        try
        {
            var source = await ResolveSourceAsync(toolId, context, cancellationToken);
            if (source is null)
            {
                return new AgentToolResult(
                    toolId,
                    $"Tool '{toolId}' is not installed.",
                    Content: $"### Tool unavailable\n\nTool '{toolId}' is not installed.",
                    IsError: true,
                    ErrorCode: "tool-not-found");
            }

            return await source.ExecuteAsync(
                context,
                new AgentToolRequest(toolId, argumentsJson),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                toolId,
                $"Tool '{toolId}' failed: {ex.Message}",
                Content: $"### Tool execution failed\n\n{ex.Message}",
                IsError: true,
                ErrorCode: AgentToolResultErrorCodes.ToolExecutionException);
        }
    }

    public async Task<AgentPermissionRequest?> BuildPermissionRequestAsync(
        string toolId,
        string argumentsJson,
        Guid? sessionId = null,
        string? profileId = null,
        AgentWorkspaceRecord? workspace = null,
        Guid? runId = null,
        long? runRevision = null,
        Guid? userTurnId = null,
        string? toolCallId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var context = new AgentToolExecutionContext(sessionId, effectiveProfileId, workspace, ResolveExecutionBinding(workspace), RunId: runId, RunRevision: runRevision, UserTurnId: userTurnId, ToolCallId: toolCallId);
        var source = await ResolveSourceAsync(toolId, context, cancellationToken);
        return source is IAgentPermissionAwareToolSource permissionAwareSource
            ? await permissionAwareSource.BuildPermissionRequestAsync(context, new AgentToolRequest(toolId, argumentsJson), cancellationToken)
            : null;
    }

    private IReadOnlyList<IAgentToolSource> GetSources()
        => [
            _installedPackageToolSource,
            .. _extensionCatalog.GetExtensions(PackageExtensionPoints.ToolSources)
                .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
        ];

    private static async Task<IReadOnlyList<AgentRuntimeTool>> ListRuntimeToolsAsync(
        IAgentToolSource source,
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
    {
        if (source is IAgentNativeToolSource nativeToolSource)
        {
            return await nativeToolSource.ListRuntimeToolsAsync(context, cancellationToken);
        }

        var descriptors = await source.ListToolsAsync(context, cancellationToken);
        return descriptors.Select(CreateRuntimeTool).ToArray();
    }

    private static AgentRuntimeTool CreateRuntimeTool(AgentToolDescriptor descriptor)
        => new(
            descriptor,
            AIFunctionFactory.CreateDeclaration(
                descriptor.ToolId,
                descriptor.Description,
                ParseJsonSchema(descriptor.ArgumentsJsonSchema),
                returnJsonSchema: null));

    private static JsonElement ParseJsonSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false,
            });
        }

        using var document = JsonDocument.Parse(schemaJson);
        return document.RootElement.Clone();
    }

    private async Task<IAgentToolSource?> ResolveSourceAsync(
        string toolId,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        var profile = string.IsNullOrWhiteSpace(context.ProfileId)
            ? null
            : _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs)
                .FirstOrDefault()
                ?.GetProfile(context.ProfileId);
        var sourceContext = new AgentToolSourceContext(context.SessionId, profile, context.Workspace, context.ExecutionBinding);

        foreach (var source in GetSources())
        {
            var readiness = await source.GetReadinessAsync(toolId, sourceContext, cancellationToken);
            if (readiness is not null)
            {
                return source;
            }
        }

        return null;
    }

    private static bool IsAllowedForProfile(AgentProfileRecord? profile, AgentToolDescriptor descriptor)
    {
        if (profile is null)
        {
            return true;
        }

        var assignments = GetSelectableCapabilityAssignments(profile);
        if (descriptor.ActivationRequirement is { } requirement)
        {
            return assignments.Any(assignment => IsActivationRequirementMatch(requirement, assignment));
        }

        if (assignments.Count == 0)
        {
            return false;
        }

        return descriptor.SelectionScope == AgentToolSelectionScope.Group
            ? !string.IsNullOrWhiteSpace(descriptor.SelectionGroupId)
              && assignments.Any(assignment => string.Equals(assignment.Kind, AgentProfileSelectableCapabilityKinds.ToolGroup, StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(assignment.CapabilityId, descriptor.SelectionGroupId, StringComparison.OrdinalIgnoreCase)
                                               && IsSourceAssignmentMatch(assignment.SourceId, descriptor))
            : assignments.Any(assignment => string.Equals(assignment.Kind, AgentProfileSelectableCapabilityKinds.Tool, StringComparison.OrdinalIgnoreCase)
                                            && IsToolAssignmentMatch(assignment.CapabilityId, descriptor)
                                            && IsSourceAssignmentMatch(assignment.SourceId, descriptor));
    }

    private static IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> GetSelectableCapabilityAssignments(AgentProfileRecord profile)
        => profile.SelectableCapabilityAssignments ?? [];

    private static bool IsToolAssignmentMatch(string assignmentToolId, AgentToolDescriptor descriptor)
        => string.Equals(assignmentToolId, descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
           || (descriptor.Aliases?.Any(alias => string.Equals(assignmentToolId, alias, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static bool IsSourceAssignmentMatch(string? assignmentSourceId, AgentToolDescriptor descriptor)
        => string.IsNullOrWhiteSpace(assignmentSourceId)
           || (!string.IsNullOrWhiteSpace(descriptor.SourceId)
               && string.Equals(assignmentSourceId, descriptor.SourceId, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(descriptor.SourceKind)
               && string.Equals(assignmentSourceId, descriptor.SourceKind, StringComparison.OrdinalIgnoreCase));

    private static bool IsActivationRequirementMatch(
        AgentToolActivationRequirement requirement,
        AgentProfileSelectableCapabilityAssignmentRecord assignment)
        => !string.IsNullOrWhiteSpace(requirement.CapabilityKind)
           && string.Equals(assignment.Kind, requirement.CapabilityKind, StringComparison.OrdinalIgnoreCase)
           && (string.IsNullOrWhiteSpace(requirement.SourceId)
               || (!string.IsNullOrWhiteSpace(assignment.SourceId)
                   && string.Equals(assignment.SourceId, requirement.SourceId, StringComparison.OrdinalIgnoreCase)))
           && (string.IsNullOrWhiteSpace(requirement.CapabilityId)
               || string.Equals(assignment.CapabilityId, requirement.CapabilityId, StringComparison.OrdinalIgnoreCase));

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord? workspace)
        => workspace is null
            ? null
            : _workspaceService.ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(binding => binding.IsEnabled
                                           && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase)
                                           && _executionTargetService.ResolveTarget(binding) is not null);
}

public sealed record AgentToolCatalogEntry(
    AgentToolDescriptor Descriptor,
    AgentToolReadiness Readiness);

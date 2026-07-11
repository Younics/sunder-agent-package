using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
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
            foreach (var listedDescriptor in descriptors.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var descriptor = WithSourceIdentity(source, listedDescriptor);
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
                .Select(descriptor => WithSourceIdentity(source, descriptor))
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
                var descriptor = WithSourceIdentity(source, runtimeTool.Descriptor);
                var readiness = await source.GetReadinessAsync(descriptor.ToolId, context, cancellationToken)
                    ?? new AgentToolReadiness(descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready.");
                if (readiness.Status != AgentToolReadinessStatus.Ready || !IsAllowedForProfile(effectiveProfile, descriptor))
                {
                    continue;
                }

                tools.Add(runtimeTool with { Descriptor = descriptor });
            }
        }

        return tools
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<AgentToolResult> ExecuteAsync(
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
        CancellationToken cancellationToken = default,
        AgentToolDescriptor? advertisedDescriptor = null)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var context = new AgentToolExecutionContext(sessionId, effectiveProfileId, workspace, ResolveExecutionBinding(workspace), allowOutsideConfiguredScope, runId, runRevision, userTurnId, toolCallId);
        try
        {
            var resolvedTool = await ResolveAdvertisedToolAsync(toolId, context, advertisedDescriptor, cancellationToken);
            if (resolvedTool is null)
            {
                return new AgentToolResult(
                    toolId,
                    $"Tool '{toolId}' was not in the ready, assigned tool catalog for this run.",
                    Content: $"### Tool denied\n\nTool '{toolId}' was not advertised as ready and assigned for this run.",
                    IsError: true,
                    ErrorCode: AgentToolSecurityErrorCodes.NotAdvertised);
            }

            return await resolvedTool.Source.ExecuteAsync(
                context,
                new AgentToolRequest(toolId, argumentsJson),
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentToolResult(
                toolId,
                $"Tool '{toolId}' timed out or was canceled before it completed: {ex.Message}",
                Content: $"### Tool execution failed\n\nTool '{toolId}' timed out or was canceled before it completed.\n\n{ex.Message}",
                IsError: true,
                ErrorCode: AgentToolResultErrorCodes.ToolExecutionException);
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
        CancellationToken cancellationToken = default,
        AgentToolDescriptor? advertisedDescriptor = null)
    {
        var resolution = await ResolvePermissionRequirementAsync(
            toolId,
            argumentsJson,
            sessionId,
            profileId,
            workspace,
            runId,
            runRevision,
            userTurnId,
            toolCallId,
            advertisedDescriptor,
            cancellationToken);
        if (resolution is null)
        {
            return CreateDeniedPermissionRequest(
                toolId,
                $"Tool '{toolId}' was not in the ready, assigned tool catalog for this run.");
        }

        return string.IsNullOrWhiteSpace(resolution.DeniedReason)
            ? resolution.PermissionRequest
            : CreateDeniedPermissionRequest(toolId, resolution.DeniedReason);
    }

    internal async Task<AgentToolPermissionResolution?> ResolvePermissionRequirementAsync(
        string toolId,
        string argumentsJson,
        Guid? sessionId,
        string? profileId,
        AgentWorkspaceRecord? workspace,
        Guid? runId,
        long? runRevision,
        Guid? userTurnId,
        string? toolCallId,
        AgentToolDescriptor? advertisedDescriptor,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var executionBinding = ResolveExecutionBinding(workspace);
        var context = new AgentToolExecutionContext(
            sessionId,
            effectiveProfileId,
            workspace,
            executionBinding,
            RunId: runId,
            RunRevision: runRevision,
            UserTurnId: userTurnId,
            ToolCallId: toolCallId);
        var resolvedTool = await ResolveAdvertisedToolAsync(toolId, context, advertisedDescriptor, cancellationToken);
        if (resolvedTool is null)
        {
            return null;
        }

        var permissionRequest = resolvedTool.Source is IAgentPermissionAwareToolSource permissionAwareSource
            ? await permissionAwareSource.BuildPermissionRequestAsync(
                context,
                new AgentToolRequest(toolId, argumentsJson),
                cancellationToken)
            : null;

        string? deniedReason = null;
        if (permissionRequest is null && !resolvedTool.Descriptor.IsReadOnly)
        {
            if (sessionId is null
                || runId is null
                || runRevision is null
                || string.IsNullOrWhiteSpace(toolCallId)
                || workspace is null
                || string.IsNullOrWhiteSpace(resolvedTool.Descriptor.SourceId))
            {
                deniedReason = $"Mutating tool '{toolId}' has no permission policy and the execution context is insufficient for a durable approval.";
            }
            else
            {
                permissionRequest = new AgentPermissionRequest(
                    AgentPermissionService.GenericMutationActionId,
                    AgentPermissionService.GenericMutationBoundaryId,
                    $"Allow mutating tool '{resolvedTool.Descriptor.DisplayName}' to run?",
                    ToolId: resolvedTool.Descriptor.ToolId,
                    WorkspaceId: workspace.WorkspaceId,
                    BindingId: executionBinding?.BindingId,
                    ResourceDisplayName: resolvedTool.Descriptor.DisplayName,
                    ResourceReference: $"{resolvedTool.Descriptor.SourceKind}:{resolvedTool.Descriptor.SourceId}:{resolvedTool.Descriptor.ToolId}",
                    IsMutation: true);
            }
        }

        if (permissionRequest is not null)
        {
            permissionRequest = permissionRequest with
            {
                ToolId = string.IsNullOrWhiteSpace(permissionRequest.ToolId)
                    ? resolvedTool.Descriptor.ToolId
                    : permissionRequest.ToolId,
                WorkspaceId = string.IsNullOrWhiteSpace(permissionRequest.WorkspaceId)
                    ? workspace?.WorkspaceId
                    : permissionRequest.WorkspaceId,
                BindingId = string.IsNullOrWhiteSpace(permissionRequest.BindingId)
                    ? executionBinding?.BindingId
                    : permissionRequest.BindingId,
                IsMutation = permissionRequest.IsMutation || !resolvedTool.Descriptor.IsReadOnly,
            };
        }

        return new AgentToolPermissionResolution(
            resolvedTool.Descriptor,
            resolvedTool.Source,
            permissionRequest,
            executionBinding,
            _executionTargetService.ResolveTarget(executionBinding)?.Descriptor,
            deniedReason);
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

    private async Task<ResolvedTool?> ResolveAdvertisedToolAsync(
        string toolId,
        AgentToolExecutionContext context,
        AgentToolDescriptor? advertisedDescriptor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        var profile = ResolveProfile(context.ProfileId);
        if (!string.IsNullOrWhiteSpace(context.ProfileId) && profile is null)
        {
            return null;
        }

        var sourceContext = new AgentToolSourceContext(context.SessionId, profile, context.Workspace, context.ExecutionBinding);

        foreach (var source in GetSources())
        {
            var descriptors = await source.ListToolsAsync(sourceContext, cancellationToken);
            var descriptor = descriptors
                .Select(item => WithSourceIdentity(source, item))
                .FirstOrDefault(item => string.Equals(item.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null
                || (advertisedDescriptor is not null && !IsSameAdvertisedTool(advertisedDescriptor, descriptor))
                || !IsAllowedForProfile(profile, descriptor))
            {
                continue;
            }

            var readiness = await source.GetReadinessAsync(descriptor.ToolId, sourceContext, cancellationToken);
            if (readiness is not null && readiness.Status != AgentToolReadinessStatus.Ready)
            {
                continue;
            }

            return new ResolvedTool(source, descriptor);
        }

        return null;
    }

    private AgentProfileRecord? ResolveProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs)
                .FirstOrDefault()
                ?.GetProfile(profileId);

    private static AgentToolDescriptor WithSourceIdentity(IAgentToolSource source, AgentToolDescriptor descriptor)
        => descriptor with
        {
            SourceKind = string.IsNullOrWhiteSpace(descriptor.SourceKind) ? source.SourceKind : descriptor.SourceKind,
            SourceId = string.IsNullOrWhiteSpace(descriptor.SourceId) ? source.SourceId : descriptor.SourceId,
            SourceDisplayName = string.IsNullOrWhiteSpace(descriptor.SourceDisplayName) ? source.DisplayName : descriptor.SourceDisplayName,
        };

    private static bool IsSameAdvertisedTool(
        AgentToolDescriptor advertisedDescriptor,
        AgentToolDescriptor currentDescriptor)
        => string.Equals(advertisedDescriptor.ToolId, currentDescriptor.ToolId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(advertisedDescriptor.SourceKind, currentDescriptor.SourceKind, StringComparison.OrdinalIgnoreCase)
           && string.Equals(advertisedDescriptor.SourceId, currentDescriptor.SourceId, StringComparison.OrdinalIgnoreCase)
           && advertisedDescriptor.IsReadOnly == currentDescriptor.IsReadOnly;

    private static AgentPermissionRequest CreateDeniedPermissionRequest(string toolId, string summary)
        => new(
            string.Empty,
            AgentPermissionBoundaryIds.Unknown,
            summary,
            ToolId: toolId);

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

    private sealed record ResolvedTool(IAgentToolSource Source, AgentToolDescriptor Descriptor);
}

public sealed record AgentToolCatalogEntry(
    AgentToolDescriptor Descriptor,
    AgentToolReadiness Readiness);

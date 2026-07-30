using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService(
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
    private readonly IPackageExtensionInvocationCatalog _invocationCatalog =
        AgentExtensionInvocation.Require(extensionCatalog);
    private readonly ConcurrentDictionary<Guid, AgentToolInvocationReference> _preparedInvocations = new();
    private readonly ConcurrentDictionary<Guid, PreparedResourceAuthority> _preparedResourceCapabilities = new();

    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListToolCatalogAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        bool includeUnavailable = true,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        var context = CreateSourceContext(sessionId, effectiveProfile, workspace);
        var catalog = new List<AgentToolCatalogEntry>();
        foreach (var candidate in await ListOwnedRuntimeToolCandidatesAsync(context, cancellationToken)
                     .ConfigureAwait(false))
        {
            var descriptor = candidate.RuntimeTool.Descriptor;
            AgentToolReadiness? readiness;
            try
            {
                readiness = await GetReadinessAsync(candidate, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentPackageUnavailableException)
            {
                continue;
            }
            readiness ??= new AgentToolReadiness(
                descriptor.ToolId,
                AgentToolReadinessStatus.Ready,
                "Ready.");

            if (!includeUnavailable && readiness.Status != AgentToolReadinessStatus.Ready)
            {
                continue;
            }

            catalog.Add(new AgentToolCatalogEntry(descriptor, readiness));
        }

        return catalog
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default)
    {
        var context = CreateSourceContext(sessionId: null, profile: null, workspace: null);
        var catalog = new List<AgentToolCatalogEntry>();
        foreach (var candidate in await ListOwnedRuntimeToolCandidatesAsync(context, cancellationToken)
                     .ConfigureAwait(false))
        {
            catalog.AddRange(new[] { candidate.RuntimeTool.Descriptor }
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
        => (await ListReadyOwnedRuntimeToolsAsync(
                profile,
                sessionId,
                workspace,
                cancellationToken)
            .ConfigureAwait(false))
            .Select(static tool => tool.RuntimeTool)
            .ToArray();

    internal async Task<IReadOnlyList<AgentOwnedRuntimeTool>> ListReadyOwnedRuntimeToolsAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        var context = CreateSourceContext(sessionId, effectiveProfile, workspace);
        var candidates = await ListOwnedRuntimeToolCandidatesAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var tools = new List<AgentOwnedRuntimeTool>();
        foreach (var candidate in candidates
                     .GroupBy(static item => item.RuntimeTool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() == 1)
                     .Select(static group => group.Single()))
        {
            var descriptor = candidate.RuntimeTool.Descriptor;
            AgentToolReadiness? readiness;
            try
            {
                readiness = await GetReadinessAsync(candidate, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentPackageUnavailableException)
            {
                continue;
            }
            readiness ??= new AgentToolReadiness(
                descriptor.ToolId,
                AgentToolReadinessStatus.Ready,
                "Ready.");
            if (readiness.Status != AgentToolReadinessStatus.Ready
                || !IsAllowedForProfile(effectiveProfile, descriptor))
            {
                continue;
            }

            tools.Add(new AgentOwnedRuntimeTool(
                candidate.RuntimeTool,
                candidate.Invocation.OwnerPackageId,
                candidate.Invocation));
        }

        return tools
            .OrderByDescending(item => item.RuntimeTool.Descriptor.Priority)
            .ThenBy(item => item.RuntimeTool.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RuntimeTool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<AgentToolInvocationReference?> ResolvePreparedInvocationAsync(
        AgentToolDescriptor advertisedDescriptor,
        string ownerPackageId,
        string? executionTargetOwnerPackageId,
        AgentProfileRecord profile,
        Guid sessionId,
        AgentWorkspaceRecord workspace,
        string? executionTargetConfigurationGeneration,
        CancellationToken cancellationToken)
    {
        var context = CreateSourceContext(sessionId, profile, workspace) with
        {
            ExecutionTargetConfigurationGeneration = executionTargetConfigurationGeneration,
        };
        var matches = (await ListOwnedRuntimeToolCandidatesAsync(context, cancellationToken)
                .ConfigureAwait(false))
            .Where(candidate => IsSameAdvertisedTool(advertisedDescriptor, candidate.RuntimeTool.Descriptor)
                                && string.Equals(
                                    ownerPackageId,
                                    candidate.Invocation.OwnerPackageId,
                                    StringComparison.Ordinal)
                                && string.Equals(
                                    executionTargetOwnerPackageId,
                                    candidate.Invocation.ExecutionTargetOwnerPackageId,
                                    StringComparison.Ordinal)
                                && IsAllowedForProfile(profile, candidate.RuntimeTool.Descriptor)
                                && IsInvocationAvailable(candidate.Invocation))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0].Invocation : null;
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
        IReadOnlyList<string>? approvedResourceReferences = null,
        IReadOnlyList<AgentResourceClaim>? approvedResourceClaims = null,
        IReadOnlyList<string>? approvedResourceCapabilities = null,
        CancellationToken cancellationToken = default,
        AgentToolDescriptor? advertisedDescriptor = null,
        string? advertisedOwnerPackageId = null,
        AgentToolInvocationReference? advertisedInvocation = null,
        AgentWorkspaceBindingRecord? expectedExecutionBinding = null,
        string? expectedExecutionTargetConfigurationGeneration = null,
        bool enforceExpectedExecutionContext = false)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var executionBinding = ResolveExecutionBinding(workspace);
        if (!IsCurrentWorkspaceExecutionContext(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding))
        {
            return PermissionContextChangedResult(toolId);
        }
        var context = new AgentToolExecutionContext(sessionId, effectiveProfileId, workspace, executionBinding, allowOutsideConfiguredScope, runId, runRevision, userTurnId, toolCallId)
        {
            TranscriptEpoch = ResolveTranscriptEpoch(sessionId),
            ApprovedResourceReferences = approvedResourceReferences ?? [],
            ApprovedResourceClaims = approvedResourceClaims ?? [],
            ApprovedResourceCapabilities = approvedResourceCapabilities ?? [],
            ExecutionTargetReference = ResolveExecutionTargetReference(executionBinding, advertisedInvocation),
            ExecutionTargetConfigurationGeneration = expectedExecutionTargetConfigurationGeneration,
        };
        try
        {
            var resolvedTool = await ResolveAdvertisedToolAsync(
                toolId,
                context,
                advertisedDescriptor,
                advertisedOwnerPackageId,
                advertisedInvocation,
                cancellationToken);
            if (resolvedTool is null)
            {
                return new AgentToolResult(
                    toolId,
                    $"Tool '{toolId}' was not in the ready, assigned tool catalog for this run.",
                    Content: $"### Tool denied\n\nTool '{toolId}' was not advertised as ready and assigned for this run.",
                    IsError: true,
                    ErrorCode: AgentToolSecurityErrorCodes.NotAdvertised);
            }
            if (!await IsCurrentExecutionTargetConfigurationAsync(
                    context,
                    resolvedTool.Invocation,
                    cancellationToken).ConfigureAwait(false))
            {
                return PermissionContextChangedResult(toolId);
            }
            var executionContext = context with
            {
                ResourceOperation = CreateResourceOperation(
                    context,
                    resolvedTool.Invocation,
                    ResolveActionId(toolId),
                    canIssueOutsideAuthority: false),
            };
            var request = new AgentToolRequest(toolId, argumentsJson);
            return await StartWorkspaceBoundInvocation(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding,
                PermissionContextChangedResult(toolId),
                () => InvokeToolAsync(
                    resolvedTool.Invocation,
                    cancellationToken,
                    (installedTool, token) => installedTool.ExecuteAsync(executionContext, request, token),
                    (source, token) => source.ExecuteAsync(executionContext, request, token))).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException ex)
        {
            return PackageUnavailableResult(toolId, ex.Message);
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

    internal async Task<AgentToolResult?> PreflightExecutionAsync(
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
        IReadOnlyList<string>? approvedResourceReferences = null,
        IReadOnlyList<AgentResourceClaim>? approvedResourceClaims = null,
        IReadOnlyList<string>? approvedResourceCapabilities = null,
        CancellationToken cancellationToken = default,
        AgentToolDescriptor? advertisedDescriptor = null,
        string? advertisedOwnerPackageId = null,
        AgentToolInvocationReference? advertisedInvocation = null,
        AgentWorkspaceBindingRecord? expectedExecutionBinding = null,
        string? expectedExecutionTargetConfigurationGeneration = null,
        bool enforceExpectedExecutionContext = false)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var executionBinding = ResolveExecutionBinding(workspace);
        if (!IsCurrentWorkspaceExecutionContext(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding))
        {
            return PermissionContextChangedResult(toolId);
        }
        var context = new AgentToolExecutionContext(
            sessionId,
            effectiveProfileId,
            workspace,
            executionBinding,
            allowOutsideConfiguredScope,
            runId,
            runRevision,
            userTurnId,
            toolCallId)
        {
            TranscriptEpoch = ResolveTranscriptEpoch(sessionId),
            ApprovedResourceReferences = approvedResourceReferences ?? [],
            ApprovedResourceClaims = approvedResourceClaims ?? [],
            ApprovedResourceCapabilities = approvedResourceCapabilities ?? [],
            ExecutionTargetReference = ResolveExecutionTargetReference(executionBinding, advertisedInvocation),
            ExecutionTargetConfigurationGeneration = expectedExecutionTargetConfigurationGeneration,
        };
        ResolvedTool? resolvedTool;
        try
        {
            resolvedTool = await ResolveAdvertisedToolAsync(
                toolId,
                context,
                advertisedDescriptor,
                advertisedOwnerPackageId,
                advertisedInvocation,
                cancellationToken);
        }
        catch (AgentPackageUnavailableException ex)
        {
            return PackageUnavailableResult(toolId, ex.Message);
        }
        if (resolvedTool is null)
        {
            return null;
        }
        try
        {
            if (!await IsCurrentExecutionTargetConfigurationAsync(
                    context,
                    resolvedTool.Invocation,
                    cancellationToken).ConfigureAwait(false))
            {
                return PermissionContextChangedResult(toolId);
            }
        }
        catch (AgentPackageUnavailableException ex)
        {
            return PackageUnavailableResult(toolId, ex.Message);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PermissionContextChangedResult(toolId);
        }
        if (!resolvedTool.Invocation.SupportsPreflight)
        {
            return null;
        }
        AgentToolResult? result;
        try
        {
            var executionContext = context with
            {
                ResourceOperation = CreateResourceOperation(
                    context,
                    resolvedTool.Invocation,
                    ResolveActionId(toolId),
                    canIssueOutsideAuthority: false),
            };
            result = await StartWorkspaceBoundInvocation<AgentToolResult?>(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding,
                PermissionContextChangedResult(toolId),
                () => InvokeSourceAsync(
                    resolvedTool.Invocation,
                    cancellationToken,
                    (source, token) => ((IAgentToolExecutionPreflightSource)source)
                        .PreflightExecutionAsync(
                            executionContext,
                            new AgentToolRequest(toolId, argumentsJson),
                            token))).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException ex)
        {
            return PackageUnavailableResult(toolId, ex.Message);
        }
        if (result is null || result.IsError)
        {
            return result;
        }

        return new AgentToolResult(
            toolId,
            "Tool preflight returned an invalid non-error skip result.",
            Content: "### Tool not dispatched\n\nThe tool preflight did not return a valid failure result, so execution was skipped.",
            IsError: true,
            ErrorCode: AgentToolResultErrorCodes.ToolExecutionException);
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
            advertisedDescriptor: advertisedDescriptor,
            advertisedOwnerPackageId: null,
            cancellationToken: cancellationToken);
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
        string? advertisedOwnerPackageId = null,
        AgentToolInvocationReference? advertisedInvocation = null,
        CancellationToken cancellationToken = default,
        bool issueOutsideResourceAuthority = true,
        AgentWorkspaceBindingRecord? expectedExecutionBinding = null,
        bool enforceExpectedExecutionContext = false,
        bool requireReadiness = true)
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
            ToolCallId: toolCallId)
        {
            TranscriptEpoch = ResolveTranscriptEpoch(sessionId),
            ExecutionTargetReference = ResolveExecutionTargetReference(executionBinding, advertisedInvocation),
        };
        ResolvedTool? resolvedTool;
        try
        {
            resolvedTool = await ResolveAdvertisedToolAsync(
                toolId,
                context,
                advertisedDescriptor,
                advertisedOwnerPackageId,
                advertisedInvocation,
                cancellationToken,
                requireReadiness);
        }
        catch (AgentPackageUnavailableException ex)
        {
            if (advertisedDescriptor is null || advertisedInvocation is null)
            {
                return null;
            }

            return new AgentToolPermissionResolution(
                advertisedDescriptor,
                PermissionRequest: null,
                executionBinding,
                advertisedInvocation.ExecutionTarget,
                advertisedInvocation.OwnerPackageId,
                ex.Message);
        }
        if (resolvedTool is null)
        {
            return null;
        }
        if (!IsCurrentWorkspaceExecutionContext(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding))
        {
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                "The workspace paths or execution binding changed before permission planning began.");
        }

        string? executionTargetConfigurationGeneration;
        try
        {
            executionTargetConfigurationGeneration = await GetExecutionTargetConfigurationGenerationAsync(
                context,
                resolvedTool.Invocation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException ex)
        {
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                $"The execution-target configuration could not be captured safely: {ex.Message}");
        }

        context = context with
        {
            ExecutionTargetConfigurationGeneration = executionTargetConfigurationGeneration,
            ResourceOperation = CreateResourceOperation(
                context,
                resolvedTool.Invocation,
                actionId: string.Empty,
                canIssueOutsideAuthority: issueOutsideResourceAuthority),
        };

        AgentPermissionRequest? permissionRequest;
        try
        {
            if (resolvedTool.Invocation.SupportsInstalledPermission)
            {
                permissionRequest = await InvokeInstalledToolAsync(
                    resolvedTool.Invocation,
                    cancellationToken,
                    (tool, token) => ((IAgentPermissionAwareTool)tool).BuildPermissionRequestAsync(
                        context,
                        new AgentToolRequest(toolId, argumentsJson),
                        token)).ConfigureAwait(false);
            }
            else if (resolvedTool.Invocation.SupportsSourcePermission)
            {
                permissionRequest = await InvokeSourceAsync(
                    resolvedTool.Invocation,
                    cancellationToken,
                    (source, token) => ((IAgentPermissionAwareToolSource)source).BuildPermissionRequestAsync(
                        context,
                        new AgentToolRequest(toolId, argumentsJson),
                        token)).ConfigureAwait(false);
            }
            else
            {
                permissionRequest = null;
            }
        }
        catch (AgentPackageUnavailableException ex)
        {
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                ex.Message);
        }

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

        string? currentExecutionTargetConfigurationGeneration;
        try
        {
            currentExecutionTargetConfigurationGeneration = await GetExecutionTargetConfigurationGenerationAsync(
                context,
                resolvedTool.Invocation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentPackageUnavailableException ex)
        {
            ReleasePermissionRequestAuthority(permissionRequest, resolvedTool.Invocation);
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReleasePermissionRequestAuthority(permissionRequest, resolvedTool.Invocation);
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                $"The execution-target configuration could not be revalidated safely: {ex.Message}");
        }
        if (!string.Equals(
                executionTargetConfigurationGeneration,
                currentExecutionTargetConfigurationGeneration,
                StringComparison.Ordinal))
        {
            ReleasePermissionRequestAuthority(permissionRequest, resolvedTool.Invocation);
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                "The execution-target configuration changed during permission planning.");
        }

        if (!IsCurrentWorkspaceExecutionContext(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding))
        {
            ReleasePermissionRequestAuthority(permissionRequest, resolvedTool.Invocation);
            return new AgentToolPermissionResolution(
                resolvedTool.Descriptor,
                PermissionRequest: null,
                executionBinding,
                resolvedTool.Invocation.ExecutionTarget,
                resolvedTool.Invocation.OwnerPackageId,
                "The workspace paths or execution binding changed during permission planning.");
        }

        return new AgentToolPermissionResolution(
            resolvedTool.Descriptor,
            permissionRequest,
            executionBinding,
            resolvedTool.Invocation.ExecutionTarget,
            resolvedTool.Invocation.OwnerPackageId,
            deniedReason)
        {
            ExecutionTargetConfigurationGeneration = executionTargetConfigurationGeneration,
        };
    }

}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
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
        AgentToolInvocationReference? advertisedInvocation = null)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var executionBinding = ResolveExecutionBinding(workspace);
        var context = new AgentToolExecutionContext(sessionId, effectiveProfileId, workspace, executionBinding, allowOutsideConfiguredScope, runId, runRevision, userTurnId, toolCallId)
        {
            TranscriptEpoch = ResolveTranscriptEpoch(sessionId),
            ApprovedResourceReferences = approvedResourceReferences ?? [],
            ApprovedResourceClaims = approvedResourceClaims ?? [],
            ApprovedResourceCapabilities = approvedResourceCapabilities ?? [],
            ExecutionTargetReference = ResolveExecutionTargetReference(executionBinding, advertisedInvocation),
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

            var executionContext = context with
            {
                ResourceOperation = CreateResourceOperation(
                    context,
                    resolvedTool.Invocation,
                    ResolveActionId(toolId),
                    canIssueOutsideAuthority: false),
            };
            var request = new AgentToolRequest(toolId, argumentsJson);
            return await InvokeToolAsync(
                resolvedTool.Invocation,
                cancellationToken,
                (installedTool, token) => installedTool.ExecuteAsync(executionContext, request, token),
                (source, token) => source.ExecuteAsync(executionContext, request, token)).ConfigureAwait(false);
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
        AgentToolInvocationReference? advertisedInvocation = null)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var executionBinding = ResolveExecutionBinding(workspace);
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
        if (resolvedTool is null || !resolvedTool.Invocation.SupportsPreflight)
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
            result = await InvokeSourceAsync(
                resolvedTool.Invocation,
                cancellationToken,
                (source, token) => ((IAgentToolExecutionPreflightSource)source)
                    .PreflightExecutionAsync(
                        executionContext,
                        new AgentToolRequest(toolId, argumentsJson),
                        token)).ConfigureAwait(false);
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
        bool issueOutsideResourceAuthority = true)
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
                cancellationToken);
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

        context = context with
        {
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

        return new AgentToolPermissionResolution(
            resolvedTool.Descriptor,
            permissionRequest,
            executionBinding,
            resolvedTool.Invocation.ExecutionTarget,
            resolvedTool.Invocation.OwnerPackageId,
            deniedReason);
    }

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
        string? advertisedOwnerPackageId,
        AgentToolInvocationReference? advertisedInvocation,
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

        var sourceContext = new AgentToolSourceContext(context.SessionId, profile, context.Workspace, context.ExecutionBinding)
        {
            ExecutionTargetReference = context.ExecutionTargetReference,
        };
        var matching = advertisedInvocation is null
            ? (await ListOwnedRuntimeToolCandidatesAsync(sourceContext, cancellationToken)
                    .ConfigureAwait(false))
                .Where(candidate => string.Equals(
                    candidate.RuntimeTool.Descriptor.ToolId,
                    toolId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : string.Equals(
                advertisedInvocation.Descriptor.ToolId,
                toolId,
                StringComparison.OrdinalIgnoreCase)
                ? [new OwnedRuntimeToolCandidate(
                    CreateRuntimeTool(advertisedInvocation.Descriptor),
                    advertisedInvocation)]
                : [];
        if (matching.Length != 1)
        {
            return null;
        }

        var candidate = matching[0];
        var descriptor = candidate.RuntimeTool.Descriptor;
        if (advertisedDescriptor is not null && !IsSameAdvertisedTool(advertisedDescriptor, descriptor)
            || advertisedOwnerPackageId is not null
               && !string.Equals(
                   advertisedOwnerPackageId,
                    candidate.Invocation.OwnerPackageId,
                   StringComparison.Ordinal)
            || !IsAllowedForProfile(profile, descriptor))
        {
            return null;
        }

        var readiness = await GetReadinessAsync(candidate, sourceContext, cancellationToken)
            .ConfigureAwait(false);
        return readiness is not null && readiness.Status != AgentToolReadinessStatus.Ready
            ? null
            : new ResolvedTool(descriptor, candidate.Invocation);
    }

    private async Task<IReadOnlyList<OwnedRuntimeToolCandidate>> ListOwnedRuntimeToolCandidatesAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
    {
        var candidates = new List<OwnedRuntimeToolCandidate>();
        var installedSource = new AgentToolSourceMetadata(
            _installedPackageToolSource.SourceId,
            _installedPackageToolSource.DisplayName,
            _installedPackageToolSource.SourceKind,
            SupportsPermission: false,
            SupportsPreflight: false);
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.Tools))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                var descriptor = WithSourceIdentity(installedSource, lease.Contribution.Descriptor);
                if (lease.RetirementToken.IsCancellationRequested)
                {
                    continue;
                }

                var targetSnapshot = SnapshotExecutionTarget(context.ExecutionTargetReference);
                var invocation = new AgentToolInvocationReference(
                    lease.PackageId,
                    descriptor,
                    reference,
                    SourceReference: null,
                    SupportsInstalledPermission: lease.Contribution is IAgentPermissionAwareTool,
                    SupportsSourcePermission: false,
                    SupportsPreflight: false,
                    context.ExecutionTargetReference,
                    targetSnapshot?.Descriptor,
                    targetSnapshot?.OwnerPackageId,
                    Guid.NewGuid().ToString("N"));
                candidates.Add(new OwnedRuntimeToolCandidate(
                    CreateRuntimeTool(descriptor),
                    invocation));
            }
        }

        var sourceReferences = AgentExtensionInvocation.Snapshot(
            _invocationCatalog,
            PackageExtensionPoints.ToolSources,
            static source => new AgentToolSourceMetadata(
                source.SourceId,
                source.DisplayName,
                source.SourceKind,
                source is IAgentPermissionAwareToolSource,
                source is IAgentToolExecutionPreflightSource));
        foreach (var sourceReference in sourceReferences
                     .OrderBy(source => source.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<AgentRuntimeTool> runtimeTools;
            try
            {
                runtimeTools = await InvokeTargetBoundAsync(
                    context.ExecutionTargetReference,
                    cancellationToken,
                    token => AgentExtensionInvocation.InvokeAsync(
                        sourceReference,
                        token,
                        (source, invocationToken) => new ValueTask<IReadOnlyList<AgentRuntimeTool>>(
                            ListRuntimeToolsAsync(source, context, invocationToken)))).ConfigureAwait(false);
            }
            catch (AgentPackageUnavailableException)
            {
                continue;
            }

            foreach (var runtimeTool in runtimeTools)
            {
                var descriptor = WithSourceIdentity(sourceReference.Metadata, runtimeTool.Descriptor);
                var targetSnapshot = SnapshotExecutionTarget(context.ExecutionTargetReference);
                var invocation = new AgentToolInvocationReference(
                    sourceReference.PackageId,
                    descriptor,
                    ToolReference: null,
                    sourceReference.Reference,
                    SupportsInstalledPermission: false,
                    sourceReference.Metadata.SupportsPermission,
                    sourceReference.Metadata.SupportsPreflight,
                    context.ExecutionTargetReference,
                    targetSnapshot?.Descriptor,
                    targetSnapshot?.OwnerPackageId,
                    Guid.NewGuid().ToString("N"));
                candidates.Add(new OwnedRuntimeToolCandidate(
                    CreateRuntimeTool(descriptor),
                    invocation));
            }
        }
        return candidates;
    }

    private static ValueTask<AgentToolReadiness?> GetReadinessAsync(
        OwnedRuntimeToolCandidate candidate,
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
        => InvokeToolAsync<AgentToolReadiness?>(
            candidate.Invocation,
            cancellationToken,
            static async (installedTool, token) =>
                await installedTool.GetReadinessAsync(token).ConfigureAwait(false),
            (source, token) => source.GetReadinessAsync(
                candidate.RuntimeTool.Descriptor.ToolId,
                context,
                token));

}

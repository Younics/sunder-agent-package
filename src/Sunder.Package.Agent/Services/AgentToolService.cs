using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService(
    AgentSessionService sessionService,
    AgentWorkspaceService workspaceService,
    AgentExecutionTargetService executionTargetService,
    IServiceProviders<AgentServices.IToolSourceService> sources,
    IServiceProviders<AgentServices.IToolExecutionObservedService> observedExecutions)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentExecutionTargetService _executionTargetService = executionTargetService;
    private readonly IServiceProviders<AgentServices.IToolSourceService> _sources = sources;
    private readonly IServiceProviders<AgentServices.IToolExecutionObservedService> _observedExecutions = observedExecutions;
    private readonly ConcurrentDictionary<Guid, AgentToolInvocationReference> _preparedInvocations = new();
    private readonly ConcurrentDictionary<Guid, PreparedResourceAuthority> _preparedResourceCapabilities = new();

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
        var (sourceContext, executionTarget) = await CreateSourceContextAsync(
                sessionId,
                profile,
                workspace,
                cancellationToken)
            .ConfigureAwait(false);
        var context = sourceContext with
        {
            ExecutionTargetConfigurationGeneration = executionTargetConfigurationGeneration,
        };
        var matches = (await ListOwnedRuntimeToolCandidatesAsync(context, executionTarget, cancellationToken)
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
        bool enforceExpectedExecutionContext = false,
        Action? dispatchStarted = null)
    {
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId;
        var executionBinding = ResolveExecutionBinding(workspace);
        var executionTarget = await ResolveExecutionTargetAsync(executionBinding, advertisedInvocation, cancellationToken).ConfigureAwait(false);
        var dispatchObserved = 0;
        Action? observeDispatch = dispatchStarted is null
            ? null
            : () =>
            {
                dispatchStarted();
                Volatile.Write(ref dispatchObserved, 1);
            };
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
            ExecutionTarget = executionTarget?.Reference,
            ExecutionTargetConfigurationGeneration = expectedExecutionTargetConfigurationGeneration,
            DispatchStarted = observeDispatch,
        };
        try
        {
            var resolvedTool = await ResolveAdvertisedToolAsync(
                toolId,
                context,
                advertisedDescriptor,
                advertisedOwnerPackageId,
                advertisedInvocation,
                executionTarget,
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
            await resolvedTool.Invocation.SourceProvider.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (resolvedTool.Invocation.ExecutionTargetProvider is { } target)
                await target.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (resolvedTool.Invocation.ObservedExecutionProvider is { } observed)
                await observed.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            return await StartWorkspaceBoundInvocation(
                workspace,
                enforceExpectedExecutionContext ? expectedExecutionBinding : executionBinding,
                PermissionContextChangedResult(toolId),
                () => ExecuteToolAsync(
                    resolvedTool.Invocation,
                    executionContext,
                    request,
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && (ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            && (Volatile.Read(ref dispatchObserved) != 0
                || ex.Error.DispatchState == SunderRpcDispatchState.PossiblyDispatched
                || ex.Error.RetrySafety == SunderRpcRetrySafety.Unsafe))
        {
            return ToolTransportOutcomeAmbiguousResult(toolId, ex.Message);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return PackageUnavailableResult(toolId, ex.Message);
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref dispatchObserved) != 0)
        {
            return ToolTransportOutcomeAmbiguousResult(toolId, ex.Message);
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
        catch (Exception ex) when (Volatile.Read(ref dispatchObserved) != 0)
        {
            return ToolTransportOutcomeAmbiguousResult(toolId, ex.Message);
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

    private static AgentToolResult ToolTransportOutcomeAmbiguousResult(string toolId, string message)
        => new(
            toolId,
            $"Tool '{toolId}' was dispatched, but its worker connection ended before a definitive response was received.",
            Content: $"### Tool outcome is uncertain\n\nThe tool was dispatched, but its worker connection ended before a definitive response was received. Effects may have occurred. Verify current state before retrying.\n\n{message}",
            IsError: true,
            ErrorCode: AgentToolResultErrorCodes.ToolOutcomeAmbiguous);

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
        var executionTarget = await ResolveExecutionTargetAsync(executionBinding, advertisedInvocation, cancellationToken).ConfigureAwait(false);
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
            ExecutionTarget = executionTarget?.Reference,
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
                executionTarget,
                cancellationToken);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
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
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return PackageUnavailableResult(toolId, ex.Message);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PermissionContextChangedResult(toolId);
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
                () => resolvedTool.Invocation.SourceProvider.Service.PreflightAsync(
                    executionContext,
                    new AgentToolRequest(toolId, argumentsJson),
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
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
        var executionTarget = await ResolveExecutionTargetAsync(executionBinding, advertisedInvocation, cancellationToken).ConfigureAwait(false);
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
            ExecutionTarget = executionTarget?.Reference,
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
                executionTarget,
                cancellationToken,
                requireReadiness);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
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
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
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
            permissionRequest = await resolvedTool.Invocation.SourceProvider.Service.PermissionAsync(
                context,
                new AgentToolRequest(toolId, argumentsJson),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
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
        catch (SunderRpcException ex) when (!cancellationToken.IsCancellationRequested
            && ex.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            await ReleasePermissionRequestAuthorityAsync(permissionRequest, resolvedTool.Invocation).ConfigureAwait(false);
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
            await ReleasePermissionRequestAuthorityAsync(permissionRequest, resolvedTool.Invocation).ConfigureAwait(false);
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
            await ReleasePermissionRequestAuthorityAsync(permissionRequest, resolvedTool.Invocation).ConfigureAwait(false);
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
            await ReleasePermissionRequestAuthorityAsync(permissionRequest, resolvedTool.Invocation).ConfigureAwait(false);
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

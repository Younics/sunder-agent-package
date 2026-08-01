using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService
{
    internal bool IsCurrentWorkspaceExecutionContext(
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? executionBinding)
    {
        if (workspace is null)
        {
            return executionBinding is null;
        }
        var currentWorkspace = _workspaceService.GetWorkspace(workspace.WorkspaceId);
        if (currentWorkspace is null
            || !string.Equals(
                CreateGeneration("workspace-v1", workspace),
                CreateGeneration("workspace-v1", currentWorkspace),
                StringComparison.Ordinal))
        {
            return false;
        }

        var currentBinding = ResolveExecutionBinding(currentWorkspace);
        return executionBinding is null || currentBinding is null
            ? executionBinding is null && currentBinding is null
            : string.Equals(
                CreateGeneration("binding-v1", executionBinding),
                CreateGeneration("binding-v1", currentBinding),
                StringComparison.Ordinal);
    }

    internal async ValueTask<bool> IsCurrentExecutionTargetConfigurationAsync(
        Guid? sessionId,
        string? profileId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord? executionBinding,
        string? expectedGeneration,
        string? expectedOwnerPackageId,
        CancellationToken cancellationToken)
    {
        if (executionBinding is null)
        {
            return expectedGeneration is null && expectedOwnerPackageId is null;
        }

        var target = _executionTargetService.ResolveTargetReference(executionBinding);
        if (target is null
            || !string.Equals(target.PackageId, expectedOwnerPackageId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var current = await InvokeExecutionTargetAsync(
                target,
                cancellationToken,
                (executionTarget, token) => executionTarget.GetConfigurationGenerationAsync(
                    new AgentExecutionTargetContext(
                        sessionId,
                        profileId,
                        workspace,
                        executionBinding!),
                    token)).ConfigureAwait(false);
            return string.Equals(expectedGeneration, current?.Trim(), StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private ValueTask<TResult> StartWorkspaceBoundInvocation<TResult>(
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? expectedExecutionBinding,
        TResult contextChangedResult,
        Func<ValueTask<TResult>> start)
    {
        if (workspace is null)
        {
            return IsCurrentWorkspaceExecutionContext(workspace, expectedExecutionBinding)
                ? start()
                : ValueTask.FromResult(contextChangedResult);
        }

        lock (_workspaceService.GetExecutionContextSyncRoot(workspace.WorkspaceId))
        {
            return IsCurrentWorkspaceExecutionContext(workspace, expectedExecutionBinding)
                ? start()
                : ValueTask.FromResult(contextChangedResult);
        }
    }

    private static AgentToolResult PermissionContextChangedResult(string toolId)
        => new(
            toolId,
            "The workspace execution context changed before the tool could run.",
            Content: "### Tool not dispatched\n\nThe workspace paths or execution binding changed. Retry the tool against the current workspace configuration.",
            IsError: true,
            ErrorCode: AgentToolSecurityErrorCodes.PermissionContextChanged);

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

    private static AgentToolDescriptor WithSourceIdentity(
        AgentToolSourceMetadata source,
        AgentToolDescriptor descriptor)
        => descriptor with
        {
            SourceKind = string.IsNullOrWhiteSpace(descriptor.SourceKind) ? source.SourceKind : descriptor.SourceKind,
            SourceId = string.IsNullOrWhiteSpace(descriptor.SourceId) ? source.SourceId : descriptor.SourceId,
            SourceDisplayName = string.IsNullOrWhiteSpace(descriptor.SourceDisplayName) ? source.DisplayName : descriptor.SourceDisplayName,
            Aliases = descriptor.Aliases?.ToArray(),
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
                                            && _executionTargetService.ResolveTargetReference(binding) is not null);

    private AgentToolSourceContext CreateSourceContext(
        Guid? sessionId,
        AgentProfileRecord? profile,
        AgentWorkspaceRecord? workspace)
    {
        var executionBinding = ResolveExecutionBinding(workspace);
        return new AgentToolSourceContext(sessionId, profile, workspace, executionBinding)
        {
            ExecutionTargetReference = _executionTargetService.ResolveTargetReference(executionBinding),
        };
    }

    private AgentRpcReference<IAgentExecutionTarget>? ResolveExecutionTargetReference(
        AgentWorkspaceBindingRecord? executionBinding,
        AgentToolInvocationReference? advertisedInvocation)
        => advertisedInvocation is null
            ? _executionTargetService.ResolveTargetReference(executionBinding)
            : advertisedInvocation.ExecutionTargetReference;

    private static ExecutionTargetSnapshot? SnapshotExecutionTarget(
        AgentRpcReference<IAgentExecutionTarget>? reference)
    {
        if (reference is null || !reference.TryAcquire(out var lease))
        {
            return null;
        }

        using (lease)
        {
            return lease.RetirementToken.IsCancellationRequested
                ? null
                : new ExecutionTargetSnapshot(lease.Contribution.Descriptor, lease.PackageId);
        }
    }

    private long ResolveTranscriptEpoch(Guid? sessionId)
        => sessionId is { } id ? _sessionService.GetTranscriptEpoch(id) : 0;

    private AgentProfileRecord? ResolveProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        foreach (var reference in _rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                var profile = lease.Service.GetProfile(profileId);
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    return profile;
                }
            }
        }

        return null;
    }

    internal void BindPreparedInvocation(Guid executionId, AgentToolInvocationReference invocation)
        => _preparedInvocations[executionId] = invocation;

    internal void BindPreparedResourceCapabilities(
        Guid executionId,
        AgentPermissionRequest? permissionRequest,
        AgentToolInvocationReference invocation)
    {
        var capabilities = permissionRequest?.ResourceCapabilities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (capabilities.Length == 0)
        {
            ReleasePreparedResourceAuthority(executionId);
            return;
        }
        var authority = new PreparedResourceAuthority(
            capabilities,
            invocation.ExecutionTargetReference);
        if (_preparedResourceCapabilities.TryGetValue(executionId, out var previous))
        {
            ReleaseResourceAuthority(previous);
        }
        _preparedResourceCapabilities[executionId] = authority;
    }

    internal IReadOnlyList<string>? GetPreparedResourceCapabilities(Guid executionId)
        => _preparedResourceCapabilities.GetValueOrDefault(executionId)?.Capabilities;

    internal AgentToolInvocationReference? GetPreparedInvocation(Guid executionId)
        => _preparedInvocations.GetValueOrDefault(executionId);

    internal static bool IsInvocationAvailable(AgentToolInvocationReference invocation)
    {
        if (invocation.ExecutionTargetReference is { } targetReference)
        {
            if (!targetReference.TryAcquire(out var targetLease))
            {
                return false;
            }
            using (targetLease)
            {
                if (targetLease.RetirementToken.IsCancellationRequested)
                {
                    return false;
                }
            }
        }

        if (invocation.SourceReference is not { } sourceReference
            || !sourceReference.TryAcquire(out var sourceLease))
        {
            return false;
        }
        using (sourceLease)
        {
            return !sourceLease.RetirementToken.IsCancellationRequested;
        }
    }

    internal void ReleasePreparedInvocation(Guid executionId)
    {
        _preparedInvocations.TryRemove(executionId, out _);
        ReleasePreparedResourceAuthority(executionId);
    }

    private void ReleasePreparedResourceAuthority(Guid executionId)
    {
        if (_preparedResourceCapabilities.TryRemove(executionId, out var authority))
        {
            ReleaseResourceAuthority(authority);
        }
    }

    private static void ReleaseResourceAuthority(PreparedResourceAuthority authority)
    {
        if (authority.ExecutionTargetReference is not { } targetReference
            || !targetReference.TryAcquire(out var targetLease))
        {
            return;
        }

        using (targetLease)
        {
            if (!targetLease.RetirementToken.IsCancellationRequested
                && AgentExecutionTargetRpc.SupportsFacet(
                    targetLease.Service,
                    AgentExecutionFacetIds.ResourceAuthority)
                && targetLease.Service is IAgentResourceAuthorityExecutionTarget authorityTarget)
            {
                authorityTarget.ReleaseResourceAuthority(authority.Capabilities);
            }
        }
    }

    private static void ReleasePermissionRequestAuthority(
        AgentPermissionRequest? permissionRequest,
        AgentToolInvocationReference invocation)
    {
        var capabilities = permissionRequest?.ResourceCapabilities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (capabilities.Length > 0)
        {
            ReleaseResourceAuthority(new PreparedResourceAuthority(
                capabilities,
                invocation.ExecutionTargetReference));
        }
    }

    private static AgentResourceOperationContext? CreateResourceOperation(
        AgentToolExecutionContext context,
        AgentToolInvocationReference invocation,
        string actionId,
        bool canIssueOutsideAuthority)
    {
        if (context.RunId is not { } runId
            || context.RunRevision is not { } runRevision
            || runRevision <= 0
            || string.IsNullOrWhiteSpace(context.ToolCallId)
            || context.Workspace is null
            || context.ExecutionBinding is null
            || string.IsNullOrWhiteSpace(invocation.ExecutionTargetOwnerPackageId))
        {
            return null;
        }
        return new AgentResourceOperationContext(
            runId,
            runRevision,
            context.ToolCallId,
            actionId,
            ResourceIndex: -1,
            CreateGeneration("workspace-v1", context.Workspace),
            CreateGeneration("binding-v1", context.ExecutionBinding),
            invocation.OwnerPackageId,
            invocation.ExecutionTargetOwnerPackageId,
            invocation.AuthorityActivationId,
            AuthorityUseCount: 1,
            CanIssueOutsideAuthority: canIssueOutsideAuthority);
    }

    private static async ValueTask<string?> GetExecutionTargetConfigurationGenerationAsync(
        AgentToolExecutionContext context,
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken)
    {
        if (context.Workspace is null
            || context.ExecutionBinding is null
            || invocation.ExecutionTargetReference is not { } targetReference)
        {
            return null;
        }

        var generation = await InvokeExecutionTargetAsync(
            targetReference,
            cancellationToken,
            (target, token) => target.GetConfigurationGenerationAsync(
                new AgentExecutionTargetContext(
                    context.SessionId,
                    context.ProfileId,
                    context.Workspace,
                    context.ExecutionBinding),
                token)).ConfigureAwait(false);
        if (generation is not null && string.IsNullOrWhiteSpace(generation))
        {
            throw new InvalidOperationException(
                "The execution target returned an empty configuration generation instead of null.");
        }

        return generation?.Trim();
    }

    private static async ValueTask<bool> IsCurrentExecutionTargetConfigurationAsync(
        AgentToolExecutionContext context,
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken)
        => context.ExecutionTargetConfigurationGeneration is not { } expected
           || string.Equals(
               expected,
               await GetExecutionTargetConfigurationGenerationAsync(
                   context,
                   invocation,
                   cancellationToken).ConfigureAwait(false),
               StringComparison.Ordinal);

    private static string CreateGeneration<T>(string version, T value)
    {
        var json = AgentPermissionFingerprint.NormalizeJson(JsonSerializer.Serialize(value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version + "\n" + json)))
            .ToLowerInvariant();
    }

    private static string ResolveActionId(string toolId)
        => toolId.ToLowerInvariant() switch
        {
            "read" => "files.read",
            "grep" or "glob" => "files.search",
            "write" or "edit" or "apply_patch" => "files.mutate",
            _ => string.Empty,
        };

    private static ValueTask<TResult> InvokeToolAsync<TResult>(
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken,
        Func<IAgentToolSource, CancellationToken, ValueTask<TResult>> sourceCallback)
        => InvokeSourceAsync(invocation, cancellationToken, sourceCallback);

    private static ValueTask<TResult> InvokeSourceAsync<TResult>(
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken,
        Func<IAgentToolSource, CancellationToken, ValueTask<TResult>> callback)
        => InvokeTargetBoundAsync(
            invocation.ExecutionTargetReference,
            cancellationToken,
            token => AgentRpcInvocation.InvokeAsync(
                new AgentRpcOwnedReference<IAgentToolSource, AgentToolDescriptor>(
                    invocation.SourceReference
                    ?? throw new InvalidOperationException("Tool-source invocation reference is unavailable."),
                    invocation.OwnerPackageId,
                    invocation.Descriptor),
                token,
                callback));

    private static ValueTask<TResult> InvokeTargetBoundAsync<TResult>(
        AgentRpcReference<IAgentExecutionTarget>? targetReference,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<TResult>> callback)
        => targetReference is null
            ? callback(cancellationToken)
            : InvokeExecutionTargetAsync(
                targetReference,
                cancellationToken,
                (_, token) => callback(token));

    private static async ValueTask<TResult> InvokeExecutionTargetAsync<TResult>(
        AgentRpcReference<IAgentExecutionTarget> targetReference,
        CancellationToken cancellationToken,
        Func<IAgentExecutionTarget, CancellationToken, ValueTask<TResult>> callback)
    {
        if (!targetReference.TryAcquire(out var targetLease))
        {
            throw AgentRpcInvocation.Unavailable("execution-target");
        }

        using (targetLease)
        {
            var packageId = targetLease.PackageId;
            var retirementToken = targetLease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                var result = await callback(targetLease.Contribution, invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw AgentRpcInvocation.Unavailable(packageId);
                }
                return result;
            }
            catch (AgentPackageUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (
                retirementToken.IsCancellationRequested
                || AgentRpcInvocation.IsUnavailableFailure(exception, cancellationToken))
            {
                throw AgentRpcInvocation.Unavailable(packageId, exception);
            }
        }
    }

    private static AgentToolResult PackageUnavailableResult(string toolId, string message)
        => new(
            toolId,
            message,
            Content: $"### Tool package unavailable\n\n{message}",
            IsError: true,
            ErrorCode: AgentToolResultErrorCodes.PackageUnavailable);

    private sealed record ResolvedTool(
        AgentToolDescriptor Descriptor,
        AgentToolInvocationReference Invocation);

    private sealed record PreparedResourceAuthority(
        IReadOnlyList<string> Capabilities,
        AgentRpcReference<IAgentExecutionTarget>? ExecutionTargetReference);

    private sealed record OwnedRuntimeToolCandidate(
        AgentRuntimeTool RuntimeTool,
        AgentToolInvocationReference Invocation);
}

public sealed record AgentToolCatalogEntry(
    AgentToolDescriptor Descriptor,
    AgentToolReadiness Readiness);

internal sealed record AgentOwnedRuntimeTool(
    AgentRuntimeTool RuntimeTool,
    string OwnerPackageId,
    AgentToolInvocationReference Invocation,
    string? ToolSchemaId = null,
    string? ToolSchemaVersion = null);

internal sealed record AgentToolInvocationReference(
    string OwnerPackageId,
    AgentToolDescriptor Descriptor,
    AgentRpcReference<IAgentToolSource>? SourceReference,
    bool SupportsSourcePermission,
    bool SupportsPreflight,
    AgentRpcReference<IAgentExecutionTarget>? ExecutionTargetReference,
    AgentExecutionTargetDescriptor? ExecutionTarget,
    string? ExecutionTargetOwnerPackageId,
    string AuthorityActivationId);

internal sealed record ExecutionTargetSnapshot(
    AgentExecutionTargetDescriptor Descriptor,
    string OwnerPackageId);

internal sealed record AgentToolSourceMetadata(
    string SourceId,
    string DisplayName,
    string SourceKind,
    bool SupportsPermission,
    bool SupportsPreflight);

using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService
{
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
            ExecutionTargetReference = _executionTargetService.ResolveTargetReference(executionBinding)?.Reference,
        };
    }

    private IPackageExtensionReference<IAgentExecutionTarget>? ResolveExecutionTargetReference(
        AgentWorkspaceBindingRecord? executionBinding,
        AgentToolInvocationReference? advertisedInvocation)
        => advertisedInvocation is null
            ? _executionTargetService.ResolveTargetReference(executionBinding)?.Reference
            : advertisedInvocation.ExecutionTargetReference;

    private static ExecutionTargetSnapshot? SnapshotExecutionTarget(
        IPackageExtensionReference<IAgentExecutionTarget>? reference)
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

        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.RuntimeCatalogs))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                var profile = lease.Contribution.GetProfile(profileId);
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

        if (invocation.ToolReference is { } toolReference)
        {
            if (!toolReference.TryAcquire(out var lease))
            {
                return false;
            }
            using (lease)
            {
                return !lease.RetirementToken.IsCancellationRequested;
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
                && targetLease.Contribution is IAgentResourceAuthorityExecutionTarget authorityTarget)
            {
                authorityTarget.ReleaseResourceAuthority(authority.Capabilities);
            }
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
        Func<IAgentTool, CancellationToken, ValueTask<TResult>> installedCallback,
        Func<IAgentToolSource, CancellationToken, ValueTask<TResult>> sourceCallback)
        => invocation.ToolReference is not null
            ? InvokeInstalledToolAsync(invocation, cancellationToken, installedCallback)
            : InvokeSourceAsync(invocation, cancellationToken, sourceCallback);

    private static ValueTask<TResult> InvokeInstalledToolAsync<TResult>(
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken,
        Func<IAgentTool, CancellationToken, ValueTask<TResult>> callback)
        => InvokeTargetBoundAsync(
            invocation.ExecutionTargetReference,
            cancellationToken,
            token => AgentExtensionInvocation.InvokeAsync(
                new AgentExtensionReference<IAgentTool, AgentToolDescriptor>(
                    invocation.ToolReference
                    ?? throw new InvalidOperationException("Installed tool invocation reference is unavailable."),
                    invocation.OwnerPackageId,
                    invocation.Descriptor),
                token,
                callback));

    private static ValueTask<TResult> InvokeSourceAsync<TResult>(
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken,
        Func<IAgentToolSource, CancellationToken, ValueTask<TResult>> callback)
        => InvokeTargetBoundAsync(
            invocation.ExecutionTargetReference,
            cancellationToken,
            token => AgentExtensionInvocation.InvokeAsync(
                new AgentExtensionReference<IAgentToolSource, AgentToolDescriptor>(
                    invocation.SourceReference
                    ?? throw new InvalidOperationException("Tool-source invocation reference is unavailable."),
                    invocation.OwnerPackageId,
                    invocation.Descriptor),
                token,
                callback));

    private static async ValueTask<TResult> InvokeTargetBoundAsync<TResult>(
        IPackageExtensionReference<IAgentExecutionTarget>? targetReference,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<TResult>> callback)
    {
        if (targetReference is null)
        {
            return await callback(cancellationToken).ConfigureAwait(false);
        }
        if (!targetReference.TryAcquire(out var targetLease))
        {
            throw AgentExtensionInvocation.Unavailable("execution-target");
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
                var result = await callback(invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw AgentExtensionInvocation.Unavailable(packageId);
                }
                return result;
            }
            catch (AgentPackageUnavailableException)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw AgentExtensionInvocation.Unavailable(packageId, exception);
            }
            catch (Exception exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw AgentExtensionInvocation.Unavailable(packageId, exception);
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
        IPackageExtensionReference<IAgentExecutionTarget>? ExecutionTargetReference);

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
    IPackageExtensionReference<IAgentTool>? ToolReference,
    IPackageExtensionReference<IAgentToolSource>? SourceReference,
    bool SupportsInstalledPermission,
    bool SupportsSourcePermission,
    bool SupportsPreflight,
    IPackageExtensionReference<IAgentExecutionTarget>? ExecutionTargetReference,
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

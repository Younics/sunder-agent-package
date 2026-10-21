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
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

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

        var target = await _executionTargetService.ResolveTargetAsync(executionBinding, cancellationToken).ConfigureAwait(false);
        if (target is null
            || !string.Equals(target.PackageId, expectedOwnerPackageId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var current = await target.Service.ConfigurationGenerationAsync(
                    new AgentExecutionTargetContext(
                        sessionId,
                        profileId,
                        workspace,
                        executionBinding!),
                    cancellationToken).ConfigureAwait(false);
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
        AgentToolSourceDescriptor source,
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
                                            && string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));

    private async Task<(AgentToolSourceContext Context, ServiceProvider<AgentServices.IExecutionTargetService>? Target)> CreateSourceContextAsync(
        Guid? sessionId,
        AgentProfileRecord? profile,
        AgentWorkspaceRecord? workspace,
        CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            return (new AgentToolSourceContext(sessionId, profile, workspace, null), null);
        }

        foreach (var binding in _workspaceService.ListBindings(workspace.WorkspaceId)
                     .Where(binding => binding.IsEnabled
                                       && string.Equals(
                                           binding.Role,
                                           AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                                           StringComparison.OrdinalIgnoreCase)))
        {
            var target = await _executionTargetService.ResolveTargetAsync(
                    binding,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is not null)
            {
                return (new AgentToolSourceContext(sessionId, profile, workspace, binding)
                {
                    ExecutionTarget = target.Reference,
                }, target);
            }
        }

        return (new AgentToolSourceContext(sessionId, profile, workspace, null), null);
    }

    private ValueTask<ServiceProvider<AgentServices.IExecutionTargetService>?> ResolveExecutionTargetAsync(
        AgentWorkspaceBindingRecord? executionBinding,
        AgentToolInvocationReference? advertisedInvocation,
        CancellationToken cancellationToken)
        => advertisedInvocation is null
            ? _executionTargetService.ResolveTargetAsync(executionBinding, cancellationToken)
            : ValueTask.FromResult(advertisedInvocation.ExecutionTargetProvider);

    private static async Task<ExecutionTargetSnapshot?> SnapshotExecutionTargetAsync(
        ServiceProvider<AgentServices.IExecutionTargetService>? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return null;
        }
        var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutionTargetSnapshot(descriptor, provider.PackageId);
    }

    private long ResolveTranscriptEpoch(Guid? sessionId)
        => sessionId is { } id ? _sessionService.GetTranscriptEpoch(id) : 0;

    private AgentProfileRecord? ResolveProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : _sessionService.Store.GetProfile(profileId);

    internal void BindPreparedInvocation(Guid executionId, AgentToolInvocationReference invocation)
        => _preparedInvocations[executionId] = invocation;

    internal async ValueTask BindPreparedResourceCapabilitiesAsync(
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
            await ReleasePreparedResourceAuthorityAsync(executionId).ConfigureAwait(false);
            return;
        }
        var authority = new PreparedResourceAuthority(
            capabilities,
            invocation.ExecutionTargetProvider,
            invocation.ExecutionTarget?.SupportsFacet(AgentExecutionFacetIds.ResourceAuthority) == true);
        if (_preparedResourceCapabilities.TryGetValue(executionId, out var previous))
        {
            await ReleaseResourceAuthorityAsync(previous).ConfigureAwait(false);
        }
        _preparedResourceCapabilities[executionId] = authority;
    }

    internal IReadOnlyList<string>? GetPreparedResourceCapabilities(Guid executionId)
        => _preparedResourceCapabilities.GetValueOrDefault(executionId)?.Capabilities;

    internal AgentToolInvocationReference? GetPreparedInvocation(Guid executionId)
        => _preparedInvocations.GetValueOrDefault(executionId);

    internal static bool IsInvocationAvailable(AgentToolInvocationReference invocation)
        => !invocation.SourceProvider.RetirementToken.IsCancellationRequested
           && invocation.ExecutionTargetProvider?.RetirementToken.IsCancellationRequested != true
           && invocation.ObservedExecutionProvider?.RetirementToken.IsCancellationRequested != true;

    internal async ValueTask ReleasePreparedInvocationAsync(Guid executionId)
    {
        _preparedInvocations.TryRemove(executionId, out _);
        await ReleasePreparedResourceAuthorityAsync(executionId).ConfigureAwait(false);
    }

    private async ValueTask ReleasePreparedResourceAuthorityAsync(Guid executionId)
    {
        if (_preparedResourceCapabilities.TryRemove(executionId, out var authority))
        {
            await ReleaseResourceAuthorityAsync(authority).ConfigureAwait(false);
        }
    }

    private static async ValueTask ReleaseResourceAuthorityAsync(PreparedResourceAuthority authority)
    {
        if (!authority.SupportsResourceAuthority
            || authority.ExecutionTargetProvider is not { } target
            || target.RetirementToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // Cleanup drains even after the initiating tool operation is canceled.
            await target.Service.ReleaseResourceAuthorityAsync(authority.Capabilities, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            // Authority issued by a retired activation is already revoked.
        }
    }

    private static async ValueTask ReleasePermissionRequestAuthorityAsync(
        AgentPermissionRequest? permissionRequest,
        AgentToolInvocationReference invocation)
    {
        var capabilities = permissionRequest?.ResourceCapabilities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (capabilities.Length > 0)
        {
            await ReleaseResourceAuthorityAsync(new PreparedResourceAuthority(
                capabilities,
                invocation.ExecutionTargetProvider,
                invocation.ExecutionTarget?.SupportsFacet(AgentExecutionFacetIds.ResourceAuthority) == true)).ConfigureAwait(false);
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
            || invocation.ExecutionTargetProvider is not { } target)
        {
            return null;
        }

        var generation = await target.Service.ConfigurationGenerationAsync(
                new AgentExecutionTargetContext(
                    context.SessionId,
                    context.ProfileId,
                    context.Workspace,
                    context.ExecutionBinding),
                cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<AgentToolResult> ExecuteToolAsync(
        AgentToolInvocationReference invocation,
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.ObservedExecutionProvider is null)
        {
            context.DispatchStarted?.Invoke();
            return await invocation.SourceProvider.Service.ExecuteAsync(context, request, cancellationToken).ConfigureAwait(false);
        }

        await foreach (var item in invocation.ObservedExecutionProvider.Service.ExecuteAsync(context, request, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case AgentToolDispatchedEvent:
                    context.DispatchStarted?.Invoke();
                    break;
                case AgentToolExecutionCompletedEvent { Result: { } result }:
                    return result;
                case AgentToolExecutionCompletedEvent:
                    throw new InvalidOperationException("The observed tool execution returned an empty result event.");
                default:
                    throw new InvalidOperationException("The observed tool execution returned an unknown event.");
            }
        }
        throw new InvalidOperationException("The observed tool execution ended without a result event.");
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
        ServiceProvider<AgentServices.IExecutionTargetService>? ExecutionTargetProvider,
        bool SupportsResourceAuthority);

    private sealed record OwnedRuntimeToolCandidate(
        AgentRuntimeTool RuntimeTool,
        AgentToolInvocationReference Invocation);
}

internal sealed record AgentOwnedRuntimeTool(
    AgentRuntimeTool RuntimeTool,
    string OwnerPackageId,
    AgentToolInvocationReference Invocation,
    string? ToolSchemaId = null,
    string? ToolSchemaVersion = null);

internal sealed record AgentToolInvocationReference(
    string OwnerPackageId,
    AgentToolDescriptor Descriptor,
    ServiceProvider<AgentServices.IToolSourceService> SourceProvider,
    ServiceProvider<AgentServices.IToolExecutionObservedService>? ObservedExecutionProvider,
    ServiceProvider<AgentServices.IExecutionTargetService>? ExecutionTargetProvider,
    AgentExecutionTargetDescriptor? ExecutionTarget,
    string? ExecutionTargetOwnerPackageId,
    string AuthorityActivationId);

internal sealed record ExecutionTargetSnapshot(
    AgentExecutionTargetDescriptor Descriptor,
    string OwnerPackageId);

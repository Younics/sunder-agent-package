using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    public async Task<AgentProfileRecord> CreateProfileAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(
                AgentRuntimeOperations.Profiles,
                new AgentProfileCommand(AgentProfileCommandKind.Create, DisplayName: displayName),
                cancellationToken)
            .ConfigureAwait(false);
        InvalidateDashboard();
        return result.Profile
               ?? throw new InvalidOperationException("Runtime did not return the created profile.");
    }

    public async Task SaveProfileAsync(
        string profileId, string displayName, string? description, string? instructions,
        string? chatProviderId, string? chatModelId, string? embeddingProviderId, string? embeddingModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
        string? behaviorLoopId = null, string? behaviorLoopSourceId = null,
        string? behaviorLoopSettingsJson = null, string? chatModelSettingsJson = null,
        CancellationToken cancellationToken = default)
    {
        _ = await InvokeAsync(AgentRuntimeOperations.Profiles, new AgentProfileCommand(
                AgentProfileCommandKind.Save, profileId, displayName, description, instructions,
                chatProviderId, chatModelId, embeddingProviderId, embeddingModelId,
                selectableCapabilityAssignments, behaviorLoopId, behaviorLoopSourceId,
                behaviorLoopSettingsJson, chatModelSettingsJson), cancellationToken)
            .ConfigureAwait(false);
        InvalidateDashboard();
    }

    public async Task DeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        _ = await InvokeAsync(
                AgentRuntimeOperations.Profiles,
                new AgentProfileCommand(AgentProfileCommandKind.Delete, ProfileId: profileId),
                cancellationToken)
            .ConfigureAwait(false);
        InvalidateDashboard();
    }

    public async Task<AgentWorkspaceRecord> CreateWorkspaceAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(
                AgentRuntimeOperations.Workspaces,
                new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: displayName),
                cancellationToken)
            .ConfigureAwait(false);
        InvalidateDashboard();
        return result.Workspace
               ?? throw new InvalidOperationException("Runtime did not return the created workspace.");
    }

    public async Task SaveWorkspaceAggregateAsync(
        string workspaceId, string displayName, string? description,
        IReadOnlyList<AgentWorkspacePathRecord> paths,
        IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
        string? executionTargetId,
        CancellationToken cancellationToken = default)
    {
        _ = await InvokeAsync(AgentRuntimeOperations.Workspaces, new AgentWorkspaceCommand(
                AgentWorkspaceCommandKind.Save, workspaceId, displayName, description, paths, documents,
                executionTargetId), cancellationToken)
            .ConfigureAwait(false);
        InvalidateDashboard();
    }

    public async Task DeleteWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        _ = await InvokeAsync(
                AgentRuntimeOperations.Workspaces,
                new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Delete, WorkspaceId: workspaceId),
                cancellationToken)
            .ConfigureAwait(false);
        InvalidateDashboard();
        InvalidateSessions();
    }

    public async Task<AgentPermissionProjection> LoadGlobalPermissionsAsync(
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureGlobalPermissionGeneration();
        var projection = await InvokeAsync(
                AgentRuntimeOperations.Permissions,
                new AgentPermissionCommand(AgentPermissionCommandKind.Read),
                cancellationToken)
            .ConfigureAwait(false);
        TryCacheGlobalPermissions(projection, generation);
        return projection;
    }

    public async Task SaveOverrideAsync(
        string actionId,
        string boundaryId,
        AgentPermissionDecision decision,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureGlobalPermissionGeneration();
        var projection = await InvokeAsync(AgentRuntimeOperations.Permissions, new AgentPermissionCommand(
                AgentPermissionCommandKind.SaveOverride,
                ActionId: actionId,
                BoundaryId: boundaryId,
                Decision: decision), cancellationToken)
            .ConfigureAwait(false);
        TryCacheGlobalPermissions(projection, generation);
    }

    public async Task DeleteOverrideAsync(
        string actionId,
        string boundaryId,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureGlobalPermissionGeneration();
        var projection = await InvokeAsync(AgentRuntimeOperations.Permissions, new AgentPermissionCommand(
                AgentPermissionCommandKind.DeleteOverride,
                ActionId: actionId,
                BoundaryId: boundaryId), cancellationToken)
            .ConfigureAwait(false);
        TryCacheGlobalPermissions(projection, generation);
    }
}

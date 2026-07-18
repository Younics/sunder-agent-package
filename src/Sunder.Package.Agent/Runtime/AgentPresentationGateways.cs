using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Runtime;

public interface IAgentProfileGateway
{
    event Action<string>? ProfileChanged;
    event Action? SelectableCapabilitiesChanged;

    IReadOnlyList<AgentProfileRecord> ListProfiles();
    AgentProfileRecord? GetProfile(string profileId);
    AgentProfileModelBindingRecord? GetChatBinding(string profileId);
    Task<AgentProfileRecord> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default);
    void SaveProfile(string profileId, string displayName, string? description, string? instructions,
        string? chatProviderId, string? chatModelId, string? embeddingProviderId, string? embeddingModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
        string? behaviorLoopId = null, string? behaviorLoopSourceId = null,
        string? behaviorLoopSettingsJson = null, string? chatModelSettingsJson = null);
    void DeleteProfile(string profileId);
    IReadOnlyList<AgentBehaviorLoopDescriptor> ListBehaviorLoopDescriptors();
    IReadOnlyList<AgentProviderDescriptor> ListChatProviderDescriptors();
    IReadOnlyList<AgentEmbeddingProviderDescriptor> ListEmbeddingProviderDescriptors();
    bool HasProfileCapabilityConsumers(string capabilityKind);
    Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
        AgentProfileRecord? profile = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(string? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(string? providerId, CancellationToken cancellationToken = default);
    Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default);
    Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(string? providerId, CancellationToken cancellationToken = default);
}

public interface IAgentWorkspaceGateway
{
    event Action? WorkspacesChanged;

    IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces();
    AgentWorkspaceRecord? GetWorkspace(string workspaceId);
    AgentWorkspaceRecord CreateWorkspace(string displayName);
    void SaveWorkspace(string workspaceId, string displayName, string? description);
    void SaveWorkspaceAggregate(string workspaceId, string displayName, string? description,
        IReadOnlyList<AgentWorkspacePathRecord> paths,
        IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
        string? executionTargetId);
    void DeleteWorkspace(string workspaceId);
    IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId);
    AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(string workspaceId, string contributionId,
        string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget);
    void RemovePrimaryExecutionBinding(string workspaceId);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IAgentSessionGateway
{
    event Action<Guid>? SessionChanged;
    event Action<Guid, AgentTurnRecord>? TurnChanged;
    event Action<Guid>? TranscriptReset;
    event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged;

    IReadOnlyList<AgentSessionRecord> ListSessions();
    IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId);
    AgentSessionRecord CreateSession(string title, Guid? parentSessionId = null, Guid? rootSessionId = null,
        Guid? parentRunId = null, long? parentRunRevision = null, string? parentToolCallId = null,
        string? taskId = null, string? profileId = null, string? behaviorLoopId = null,
        string? agentKind = null, string? workspaceId = null);
    AgentSessionRecord? GetSession(Guid sessionId);
    void UpdateSession(AgentSessionRecord session);
    void DeleteSession(Guid sessionId);
    IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId);
    IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit);
    IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit);
    IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit);
    AgentTurnRecord? GetTurn(Guid turnId);
    AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId);
}

public interface IAgentTurnMutationGateway
{
    event Action<AgentTurnMutation>? TurnMutated;
}

public interface IAgentPermissionGateway
{
    AgentSessionPermissionState GetSessionState(Guid sessionId);
    void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled);
    IReadOnlyList<AgentPermissionActionDescriptor> ListActions();
    IReadOnlyList<AgentPermissionOverride> ListOverrides();
    void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision);
    void DeleteOverride(string actionId, string boundaryId);
    IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId);
    void SaveSessionApproval(Guid sessionId, string actionId, string boundaryId);
}

public interface IAgentRunGateway
{
    Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId, string userMessage,
        string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken = default);
    Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId, Guid rollbackAnchorTurnId,
        string profileId, string userMessage, string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken = default);
    Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
        CancellationToken cancellationToken = default);
    Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
        CancellationToken cancellationToken = default);
}

public interface IAgentAttachmentGateway
{
    Task<AgentAttachmentUploadRequest> LoadUploadRequestFromFileAsync(string path, CancellationToken cancellationToken = default);
    AgentAttachmentInfo InspectUpload(AgentAttachmentUploadRequest upload);
    Task<byte[]> ReadAttachmentBytesAsync(AgentAttachmentMetadata metadata, CancellationToken cancellationToken = default);
}

public interface IAgentExecutionGateway
{
    IReadOnlyList<AgentExecutionTargetDescriptor> ListTargets();
    Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken = default);
}

internal interface IAgentPresentationInitialization
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal interface IAgentExecutionTargetLoader
{
    Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(
        CancellationToken cancellationToken = default);
}

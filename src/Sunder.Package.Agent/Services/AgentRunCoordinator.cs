using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunCoordinator(
    AgentUserMessageRunCoordinator userMessageRunCoordinator,
    AgentRunStopCoordinator stopCoordinator,
    AgentChildRunSessionService childRunSessionService,
    AgentPermissionResumeCoordinator permissionResumeCoordinator) : IAgentChildRunExecutor, IAgentRunGateway
{
    private readonly AgentUserMessageRunCoordinator _userMessageRunCoordinator = userMessageRunCoordinator;
    private readonly AgentRunStopCoordinator _stopCoordinator = stopCoordinator;
    private readonly AgentChildRunSessionService _childRunSessionService = childRunSessionService;
    private readonly AgentPermissionResumeCoordinator _permissionResumeCoordinator = permissionResumeCoordinator;

    public async ValueTask<AgentChildRunResult> RunChildAsync(
        AgentChildRunRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var childRunSession = _childRunSessionService.PrepareChildSession(request);
        var checkpoint = await QueueUserMessageAsync(
            childRunSession.ChildSession.SessionId,
            childRunSession.ChildProfile.ProfileId,
            request.UserMessage,
            request.WorkspaceId,
            cancellationToken);
        return _childRunSessionService.BuildResult(childRunSession.ChildSession, checkpoint);
    }

    public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId, string userMessage, string workspaceId)
        => QueueUserMessageAsync(sessionId, profileId, userMessage, workspaceId, []);

    public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        CancellationToken cancellationToken)
        => QueueUserMessageAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            [],
            cancellationToken);

    public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments)
        => _userMessageRunCoordinator.QueueAsync(sessionId, profileId, userMessage, workspaceId, attachments);

    public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
        => _userMessageRunCoordinator.QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            cancellationToken);

    public async Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
        Guid sessionId,
        Guid rollbackAnchorTurnId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments)
        => await RollbackAndQueueUserMessageAsync(
            sessionId, rollbackAnchorTurnId, profileId, userMessage, workspaceId, attachments,
            CancellationToken.None).ConfigureAwait(false);

    public async Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
        Guid sessionId,
        Guid rollbackAnchorTurnId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopAsync(sessionId).ConfigureAwait(false);
        return await _userMessageRunCoordinator.QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId)
        => _stopCoordinator.StopAsync(sessionId);

    Task<AgentRunCheckpointRecord?> IAgentRunGateway.StopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StopAsync(sessionId);
    }

    public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId)
        => _permissionResumeCoordinator.ApproveAsync(sessionId, requestId);

    Task<AgentRunCheckpointRecord?> IAgentRunGateway.ApprovePendingPermissionAsync(
        Guid sessionId, string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ApprovePendingPermissionAsync(sessionId, requestId);
    }

    public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId)
        => _permissionResumeCoordinator.DenyAsync(sessionId, requestId);

    Task<AgentRunCheckpointRecord?> IAgentRunGateway.DenyPendingPermissionAsync(
        Guid sessionId, string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DenyPendingPermissionAsync(sessionId, requestId);
    }
}

using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunCoordinator(
    AgentUserMessageRunCoordinator userMessageRunCoordinator,
    AgentRunStopCoordinator stopCoordinator,
    AgentChildRunSessionService childRunSessionService,
    AgentPermissionResumeCoordinator permissionResumeCoordinator)
    : IAgentChildRunExecutor, IAgentRunGateway, IAgentCorrelatedRunGateway
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

    Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.QueueUserMessageAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid userTurnId,
        CancellationToken cancellationToken)
        => _userMessageRunCoordinator.QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId: null,
            userTurnId,
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
        return await _userMessageRunCoordinator.QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            cancellationToken).ConfigureAwait(false);
    }

    async Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.RollbackAndQueueUserMessageAsync(
        Guid sessionId,
        Guid rollbackAnchorTurnId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid userTurnId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _userMessageRunCoordinator.QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            userTurnId,
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

    internal Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
        Guid sessionId,
        string requestId,
        bool approveForSession,
        CancellationToken cancellationToken)
        => _permissionResumeCoordinator.ApproveAsync(
            sessionId,
            requestId,
            approveForSession,
            cancellationToken);

    Task<AgentRunCheckpointRecord?> IAgentRunGateway.ApprovePendingPermissionAsync(
        Guid sessionId, string requestId, CancellationToken cancellationToken)
    {
        return _permissionResumeCoordinator.ApproveAsync(
            sessionId,
            requestId,
            cancellationToken: cancellationToken);
    }

    public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId)
        => _permissionResumeCoordinator.DenyAsync(sessionId, requestId);

    Task<AgentRunCheckpointRecord?> IAgentRunGateway.DenyPendingPermissionAsync(
        Guid sessionId, string requestId, CancellationToken cancellationToken)
    {
        return _permissionResumeCoordinator.DenyAsync(
            sessionId,
            requestId,
            cancellationToken);
    }

    internal Task<AgentUserTurnAdmissionResult> AdmitTransferredUserTurnAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadHandle> handles,
        AgentAttachmentTransferService transferService,
        Guid userTurnId,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
        => _userMessageRunCoordinator.AdmitTransferredAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            handles,
            transferService,
            userTurnId,
            rollbackAnchorTurnId,
            cancellationToken);

    internal AgentRunCommandStatus GetCommandStatus(Guid sessionId, Guid userTurnId)
        => _userMessageRunCoordinator.GetCommandStatus(sessionId, userTurnId);

    internal void SignalDispatcher() => _userMessageRunCoordinator.SignalDispatcher();
}

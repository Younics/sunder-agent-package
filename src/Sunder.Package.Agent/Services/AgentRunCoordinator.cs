using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunCoordinator(
    AgentUserMessageRunCoordinator userMessageRunCoordinator,
    AgentRunStopCoordinator stopCoordinator,
    AgentChildRunSessionService childRunSessionService,
    AgentPermissionResumeCoordinator permissionResumeCoordinator) : IAgentChildRunExecutor
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
            request.WorkspaceId);
        return _childRunSessionService.BuildResult(childRunSession.ChildSession, checkpoint);
    }

    public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId, string userMessage, string workspaceId)
        => QueueUserMessageAsync(sessionId, profileId, userMessage, workspaceId, []);

    public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments)
        => _userMessageRunCoordinator.QueueAsync(sessionId, profileId, userMessage, workspaceId, attachments);

    public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId)
        => _stopCoordinator.StopAsync(sessionId);

    public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId)
        => _permissionResumeCoordinator.ApproveAsync(sessionId, requestId);

    public AgentRunCheckpointRecord? DenyPendingPermission(Guid sessionId, string requestId)
        => _permissionResumeCoordinator.Deny(sessionId, requestId);
}

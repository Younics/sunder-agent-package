using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services.BehaviorLoops;

namespace Sunder.Package.Agent.Services;

public sealed class AgentUserMessageRunCoordinator
{
    private readonly AgentUserTurnAdmissionService _admissionService;
    private readonly AgentRunDispatcher _dispatcher;

    internal AgentUserMessageRunCoordinator(
        AgentSessionService sessionService,
        AgentRunPreparationService preparationService,
        AgentRunStartService startService,
        AgentRunExecutionService executionService,
        AgentActiveRunRegistry activeRunRegistry,
        AgentSessionTransitionGate? transitionGate = null,
        AgentSessionDeletionFence? deletionFence = null,
        AgentUserTurnAdmissionService? admissionService = null,
        AgentRunDispatcher? dispatcher = null)
    {
        var gate = transitionGate ?? AgentSessionTransitionGate.Shared;
        var fence = deletionFence ?? AgentSessionDeletionFence.Shared;
        _admissionService = admissionService ?? new AgentUserTurnAdmissionService(
            sessionService,
            preparationService.AttachmentStore,
            activeRunRegistry,
            gate,
            fence);
        _dispatcher = dispatcher ?? new AgentRunDispatcher(
            sessionService,
            preparationService,
            startService,
            executionService,
            activeRunRegistry,
            gate,
            fence);
    }

    public AgentUserMessageRunCoordinator(
        AgentSessionService sessionService,
        AgentProfileService profileService,
        AgentWorkspaceService workspaceService,
        AgentMemoryCoordinator memoryCoordinator,
        AgentRunAttachmentStore attachmentStore,
        AgentActiveRunRegistry activeRunRegistry,
        AgentRunEventLogger runEventLogger,
        AgentRunProviderResolver providerResolver,
        AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
        AgentBehaviorLoopResolver behaviorLoopResolver,
        AgentSessionTitleService? sessionTitleService = null)
        : this(
            sessionService,
            new AgentRunPreparationService(
                sessionService,
                profileService,
                workspaceService,
                attachmentStore,
                runEventLogger,
                providerResolver,
                sessionTitleService),
            new AgentRunStartService(
                sessionService,
                activeRunRegistry,
                runEventLogger,
                sessionTitleService),
            new AgentRunExecutionService(
                sessionService,
                workspaceService,
                memoryCoordinator,
                activeRunRegistry,
                runEventLogger,
                behaviorLoopHostFactory,
                behaviorLoopResolver),
            activeRunRegistry,
            deletionFence: AgentSessionDeletionFence.Shared)
    {
    }

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId)
        => QueueAsync(sessionId, profileId, userMessage, workspaceId, []);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        CancellationToken cancellationToken)
        => QueueAsync(sessionId, profileId, userMessage, workspaceId, [], cancellationToken);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments)
        => QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId: null);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
        => QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId: null,
            cancellationToken);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid? rollbackAnchorTurnId)
        => QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            CancellationToken.None);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
        => QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            userTurnId: null,
            cancellationToken);

    internal async Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid? rollbackAnchorTurnId,
        Guid? userTurnId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var admission = await _admissionService.AdmitAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            userTurnId ?? Guid.NewGuid(),
            rollbackAnchorTurnId,
            cancellationToken).ConfigureAwait(false);
        if (admission.IsExisting
            && admission.Run.Status is not (AgentDurableRunStatus.Preparing
                or AgentDurableRunStatus.Running))
        {
            return admission.Checkpoint;
        }
        return await _dispatcher.DispatchAndWaitAsync(
            admission.Run.Key.RunId,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<AgentUserTurnAdmissionResult> AdmitTransferredAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadHandle> handles,
        AgentAttachmentTransferService transferService,
        Guid userTurnId,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
        => _admissionService.AdmitTransferredAsync(
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
        => _admissionService.GetCommandStatus(sessionId, userTurnId);

    internal void SignalDispatcher() => _dispatcher.Signal();
}

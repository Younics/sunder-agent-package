using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;

namespace Sunder.Package.Agent.Services;

public sealed class AgentUserMessageRunCoordinator
{
    private readonly AgentSessionService _sessionService;
    private readonly AgentRunPreparationService _preparationService;
    private readonly AgentRunStartService _startService;
    private readonly AgentRunExecutionService _executionService;
    private readonly AgentActiveRunRegistry _activeRunRegistry;
    private readonly AgentSessionTransitionGate _transitionGate;

    internal AgentUserMessageRunCoordinator(
        AgentSessionService sessionService,
        AgentRunPreparationService preparationService,
        AgentRunStartService startService,
        AgentRunExecutionService executionService,
        AgentActiveRunRegistry activeRunRegistry,
        AgentSessionTransitionGate? transitionGate = null)
    {
        _sessionService = sessionService;
        _preparationService = preparationService;
        _startService = startService;
        _executionService = executionService;
        _activeRunRegistry = activeRunRegistry;
        _transitionGate = transitionGate ?? AgentSessionTransitionGate.Shared;
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
                memoryCoordinator,
                attachmentStore,
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
            activeRunRegistry)
    {
    }

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId) =>
        QueueAsync(sessionId, profileId, userMessage, workspaceId, []);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        CancellationToken cancellationToken) =>
        QueueAsync(sessionId, profileId, userMessage, workspaceId, [], cancellationToken);

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments) =>
        QueueAsync(
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
        CancellationToken cancellationToken) =>
        QueueAsync(
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
        Guid? rollbackAnchorTurnId) =>
        QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            CancellationToken.None);

    public async Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
        => await QueueAsync(
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            attachments,
            rollbackAnchorTurnId,
            userTurnId: null,
            cancellationToken).ConfigureAwait(false);

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
        if (userTurnId == Guid.Empty)
        {
            throw new ArgumentException("User turn id cannot be empty.", nameof(userTurnId));
        }
        var session = _sessionService.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' was not found.");
        AgentDurableRunRecord reservedRun;
        AgentActiveRunHandle runHandle;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            reservedRun = _sessionService.ReserveRun(sessionId, profileId, userMessage);
            var runCancellationSource = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            runHandle = new AgentActiveRunHandle(
                reservedRun.Key.RunId,
                reservedRun.Key.RunRevision,
                reservedRun.StartedAtUtc,
                reservedRun.ProfileId,
                reservedRun.UserMessage,
                runCancellationSource)
            {
                DurableLease = new AgentDurableRunLease(reservedRun),
            };
            var activation = _activeRunRegistry.Activate(sessionId, runHandle);
            if (!activation.IsAccepted)
            {
                runCancellationSource.Dispose();
                throw new InvalidOperationException("The newly reserved run was rejected by the active-run registry.");
            }

            if (activation.DisplacedRun is { } displacedRun)
            {
                displacedRun.CancellationTokenSource.Cancel();
                if (displacedRun.DurableLease is { } displacedLease)
                {
                    _sessionService.TryTransitionRun(
                        displacedLease,
                        AgentRunStatus.Interrupted,
                        "Superseded by a newer user message during preparation or execution.");
                }
            }
        }

        try
        {
            var preparation = await _preparationService.PrepareAsync(
                session,
                reservedRun,
                runHandle,
                profileId,
                workspaceId,
                attachments,
                rollbackAnchorTurnId,
                runHandle.CancellationTokenSource.Token).ConfigureAwait(false);
            if (preparation is AgentRunPreparationFailed preparationFailure)
            {
                return _sessionService.TryTransitionRun(
                           runHandle.DurableLease!,
                           AgentRunStatus.Failed,
                           preparationFailure.Summary)?.Checkpoint
                       ?? GetTerminalCheckpoint(reservedRun);
            }

            var preparedPlan = ((AgentRunPrepared)preparation).Plan;
            var start = await _startService
                .StartAsync(
                    preparedPlan,
                    userTurnId ?? Guid.NewGuid(),
                    runHandle.CancellationTokenSource.Token)
                .ConfigureAwait(false);
            return start switch
            {
                AgentRunStartInterrupted interrupted => interrupted.Checkpoint,
                AgentRunStarted started => await _executionService
                    .ExecuteAsync(started)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unknown agent run start result."),
            };
        }
        catch (OperationCanceledException)
        {
            var wasCurrent = _activeRunRegistry.IsCurrent(
                sessionId,
                reservedRun.Key.RunId,
                reservedRun.Key.RunRevision);
            var summary = !wasCurrent && !cancellationToken.IsCancellationRequested
                ? "Superseded by a newer user message before provider execution started."
                : "Agent run was canceled before provider execution started.";
            var checkpoint = TryTerminateReservedRun(
                runHandle,
                AgentRunStatus.Interrupted,
                summary);
            if (cancellationToken.IsCancellationRequested || wasCurrent)
            {
                throw;
            }

            return checkpoint;
        }
        catch (Exception ex)
        {
            _ = TryTerminateReservedRun(runHandle, AgentRunStatus.Failed, ex.Message);
            throw;
        }
        finally
        {
            _activeRunRegistry.TryCleanupCurrent(
                sessionId,
                reservedRun.Key.RunId,
                reservedRun.Key.RunRevision);
            runHandle.CancellationTokenSource.Dispose();
        }
    }

    private AgentRunCheckpointRecord TryTerminateReservedRun(
        AgentActiveRunHandle runHandle,
        AgentRunStatus status,
        string summary)
    {
        try
        {
            if (runHandle.DurableLease is { } lease
                && _sessionService.TryTransitionRun(lease, status, summary) is { } transition)
            {
                return transition.Checkpoint;
            }
        }
        catch
        {
            // Preserve the original execution outcome if termination persistence also fails.
        }

        return GetTerminalCheckpoint(
            runHandle.DurableLease!.Key.SessionId,
            runHandle.RunRevision);
    }

    private AgentRunCheckpointRecord GetTerminalCheckpoint(AgentDurableRunRecord run)
        => GetTerminalCheckpoint(run.Key.SessionId, run.Key.RunRevision);

    private AgentRunCheckpointRecord GetTerminalCheckpoint(Guid sessionId, long runRevision)
    {
        return _sessionService.GetLatestCheckpoint(sessionId, runRevision)
            ?? throw new InvalidOperationException("The run ended without a durable terminal checkpoint.");
    }
}

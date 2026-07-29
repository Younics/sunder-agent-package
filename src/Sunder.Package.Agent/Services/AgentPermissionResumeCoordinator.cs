using System.Diagnostics;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed class AgentPermissionResumeCoordinator(
    AgentSessionService sessionService,
    AgentWorkspaceService workspaceService,
    AgentProfileService profileService,
    AgentPermissionService permissionService,
    AgentRunProviderResolver providerResolver,
    AgentActiveRunRegistry activeRunRegistry,
    AgentRunEventLogger runEventLogger,
    AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
    AgentBehaviorLoopResolver behaviorLoopResolver,
    AgentParentRunContinuationService parentRunContinuationService,
    AgentSessionTransitionGate? transitionGate = null,
    AgentSessionDeletionFence? deletionFence = null,
    AgentBackgroundWorkService? backgroundWork = null,
    AgentToolService? toolService = null
)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentPermissionService _permissionService = permissionService;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentBehaviorLoopHostFactory _behaviorLoopHostFactory =
        behaviorLoopHostFactory;
    private readonly AgentBehaviorLoopResolver _behaviorLoopResolver = behaviorLoopResolver;
    private readonly AgentParentRunContinuationService _parentRunContinuationService =
        parentRunContinuationService;
    private readonly AgentSessionTransitionGate _transitionGate =
        transitionGate ?? AgentSessionTransitionGate.Shared;
    private readonly AgentSessionDeletionFence _deletionFence =
        deletionFence ?? AgentSessionDeletionFence.Shared;
    private readonly AgentBackgroundWorkService? _backgroundWork = backgroundWork;
    private readonly AgentToolService? _toolService = toolService;

    public Task<AgentRunCheckpointRecord?> ApproveAsync(
        Guid sessionId,
        string requestId,
        bool approveForSession = false,
        CancellationToken cancellationToken = default)
        => _backgroundWork is null
            ? ApproveCoreAsync(sessionId, requestId, approveForSession, cancellationToken)
            : _backgroundWork.RunOwnedAsync(
                ownedToken => ApproveCoreAsync(
                    sessionId,
                    requestId,
                    approveForSession,
                    ownedToken),
                cancellationToken);

    private async Task<AgentRunCheckpointRecord?> ApproveCoreAsync(
        Guid sessionId,
        string requestId,
        bool approveForSession,
        CancellationToken cancellationToken)
    {
        AgentPendingPermissionClaimResult claim;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (_sessionService.GetSession(sessionId) is { } claimSession
                && _deletionFence.IsFenced(claimSession))
            {
                return _sessionService.GetLatestCheckpoint(sessionId);
            }
            claim = _permissionService.TryClaimPendingRequest(sessionId, requestId);
        }
        if (!claim.IsClaimed || claim.Request is not { } pending)
        {
            if (claim.Outcome == AgentPendingPermissionClaimOutcome.InvalidSuspension)
            {
                var expiration = _permissionService.ExpireActiveRequest(
                    sessionId,
                    requestId,
                    "The permission request no longer matches the current suspended run.");
                PublishPermissionFinalization(expiration.Finalization);
            }

            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        try
        {
            return await ApproveClaimedAsync(
                pending,
                approveForSession,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var runStatus = ex is OperationCanceledException
                ? AgentRunStatus.Interrupted
                : AgentRunStatus.Failed;
            var summary = ex is OperationCanceledException
                ? "Approved permission resume was canceled before execution ownership was established."
                : $"Approved permission resume failed before execution ownership was established: {ex.Message}";
            var finalization = _permissionService.FinalizeClaimedRequest(
                pending,
                AgentPendingPermissionStatus.Failed,
                runStatus,
                summary);
            PublishPermissionFinalization(finalization);
            var checkpoint = finalization?.Checkpoint;
            if (checkpoint is null)
            {
                _permissionService.CompleteClaimedRequest(
                    pending,
                    AgentPendingPermissionStatus.Failed,
                    summary);
                if (_sessionService.GetRun(pending.RunId) is { FinishedAtUtc: null } run
                    && run.Key == new AgentDurableRunKey(
                        pending.RunId,
                        pending.SessionId,
                        pending.RunRevision))
                {
                    checkpoint = _sessionService.TryTransitionRun(
                        new AgentDurableRunLease(run),
                        runStatus,
                        summary)?.Checkpoint;
                }
            }

            ReleasePreparedAuthority(pending);

            return checkpoint
                ?? _sessionService.GetLatestCheckpoint(sessionId, pending.RunRevision)
                ?? _sessionService.GetLatestCheckpoint(sessionId);
        }
    }

    private async Task<AgentRunCheckpointRecord?> ApproveClaimedAsync(
        AgentPendingPermissionRequestRecord pending,
        bool approveForSession,
        CancellationToken cancellationToken)
    {
        var sessionId = pending.SessionId;

        void Complete(AgentPendingPermissionStatus status, string summary)
        {
            _permissionService.CompleteClaimedRequest(pending, status, summary);
            ReleasePreparedAuthority(pending);
        }

        AgentRunCheckpointRecord? FinalizeSuspension(
            AgentPendingPermissionStatus status,
            AgentRunStatus runStatus,
            string summary)
        {
            var finalization = _permissionService.FinalizeClaimedRequest(
                pending,
                status,
                runStatus,
                summary);
            ReleasePreparedAuthority(pending);
            PublishPermissionFinalization(finalization);
            return finalization?.Checkpoint;
        }

        var session = _sessionService.GetSession(sessionId);
        if (session is null)
        {
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The session used for this permission request was not found.")
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        if (string.Equals(
                pending.BoundaryId,
                AgentPermissionBoundaryIds.OutsideConfiguredScope,
                StringComparison.OrdinalIgnoreCase)
            && (pending.ResourceClaimSetVersion != 1 || pending.ResourceClaims.Count == 0))
        {
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Interrupted,
                       "permission-reapproval-required: Outside resource authority is not restorable; submit the tool call for explicit reapproval.")
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        if (!string.Equals(session.WorkspaceId, pending.WorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            const string summary = "The session workspace changed after permission was requested.";
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       summary)
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        var workspace = ResolveWorkspace(pending.WorkspaceId);
        if (workspace is null)
        {
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The workspace used for this run was not found.")
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        var profile = ResolveProfile(pending.ProfileId);
        if (profile is null)
        {
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The agent used for this run was not found.")
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        using var providerSelection = _providerResolver.ResolveChatProvider(profile);
        var chatBinding = providerSelection.ChatBinding;
        if (
            !providerSelection.IsAvailable
            || chatBinding is null
            || string.IsNullOrWhiteSpace(chatBinding.ModelId)
        )
        {
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Failed,
                       AgentRunStatus.Failed,
                       "No installed provider matches this profile yet, or no model is selected.")
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        AgentActiveRunHandle? runHandle = null;
        AgentDurableRunLease? runLease = null;
        DateTimeOffset runStartedAtUtc = default;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (_deletionFence.IsFenced(session))
            {
                Complete(AgentPendingPermissionStatus.Expired, "The session is being deleted.");
                return _sessionService.GetLatestCheckpoint(sessionId);
            }
            var suspendedRun = _sessionService.GetRun(pending.RunId);
            if (suspendedRun?.Key != new AgentDurableRunKey(
                    pending.RunId,
                    pending.SessionId,
                    pending.RunRevision)
                || _permissionService.ResumeClaimedRequest(pending, suspendedRun.Epoch) is null)
            {
                const string summary = "The permission continuation was stale or had already been consumed.";
                if (FinalizeSuspension(
                        AgentPendingPermissionStatus.Expired,
                        AgentRunStatus.Interrupted,
                        summary) is null)
                {
                    Complete(AgentPendingPermissionStatus.Expired, summary);
                }

                return _sessionService.GetLatestCheckpoint(sessionId);
            }

            var resumedRun = _sessionService.GetRun(pending.RunId)
                ?? throw new InvalidOperationException("Resumed permission run was not found.");
            runStartedAtUtc = resumedRun.StartedAtUtc;
            runLease = new AgentDurableRunLease(resumedRun);
            runHandle = new AgentActiveRunHandle(
                pending.RunId,
                pending.RunRevision,
                runStartedAtUtc,
                profile.ProfileId,
                pending.UserMessage,
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    providerSelection.RetirementToken))
            {
                DurableLease = runLease,
            };
            var activation = _activeRunRegistry.Activate(sessionId, runHandle);
            if (!activation.IsAccepted)
            {
                Complete(AgentPendingPermissionStatus.Expired, "A newer run superseded this permission request.");
                _sessionService.TryTransitionRun(
                    runLease,
                    AgentRunStatus.Interrupted,
                    "A newer run superseded this permission continuation.");
                runHandle.CancellationTokenSource.Dispose();
                return _sessionService.GetLatestCheckpoint(sessionId);
            }

            if (activation.DisplacedRun is { } displacedRun)
            {
                displacedRun.CancellationTokenSource.Cancel();
                if (displacedRun.DurableLease is { } displacedLease)
                {
                    _sessionService.TryTransitionRun(
                        displacedLease,
                        AgentRunStatus.Interrupted,
                        "Interrupted by a newer permission continuation.");
                }
            }
        }

        var runCancellationToken = runHandle.CancellationTokenSource.Token;

        var resumeStopwatch = Stopwatch.StartNew();
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Information,
            sessionId,
            pending.RunId,
            pending.RunRevision,
            "permission.approved_resume.start",
            "Resuming run after permission approval.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = pending.ToolId,
                ["permission.action_id"] = pending.ActionId,
                ["permission.boundary_id"] = pending.BoundaryId,
                ["permission.request_id"] = pending.RequestId,
            }
        );
        try
        {
            var host = _behaviorLoopHostFactory.Create(
                providerSelection.Provider,
                session,
                profile,
                workspace,
                pending.RunId,
                pending.RunRevision,
                runStartedAtUtc,
                pending.UserMessage,
                pending.UserTurnId,
                ResolveExecutionBinding(workspace)
            );
            var approvedToolOutcome = await host.HandleApprovedToolCallAsync(
                    pending,
                    runCancellationToken,
                    BeginApprovedExecutionAsync
                )
                .ConfigureAwait(false);
            if (approvedToolOutcome.Kind == AgentToolCallOutcomeKind.WaitingForApproval)
            {
                Complete(
                    AgentPendingPermissionStatus.Executed,
                    approvedToolOutcome.Result?.Summary
                    ?? "Approved tool execution is waiting for a child run.");
                return approvedToolOutcome.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(sessionId);
            }

            if (approvedToolOutcome.Kind != AgentToolCallOutcomeKind.Executed)
            {
                var terminalStatus = string.Equals(
                    approvedToolOutcome.Result?.ErrorCode,
                    AgentToolSecurityErrorCodes.PermissionContextChanged,
                    StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        approvedToolOutcome.Result?.ErrorCode,
                        AgentToolSecurityErrorCodes.NotAdvertised,
                        StringComparison.OrdinalIgnoreCase)
                    ? AgentPendingPermissionStatus.Expired
                    : AgentPendingPermissionStatus.Failed;
                Complete(
                    terminalStatus,
                    approvedToolOutcome.Result?.Summary ?? "Approved tool execution did not complete.");
                _runEventLogger.LogRunEvent(
                    PackageLogLevel.Warning,
                    sessionId,
                    pending.RunId,
                    pending.RunRevision,
                    "permission.approved_resume.failed",
                    approvedToolOutcome.Kind.ToString(),
                    resumeStopwatch.ElapsedMilliseconds
                );
                var childCheckpoint = approvedToolOutcome.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(sessionId);
                if (childCheckpoint is not null)
                {
                    var terminalParentCheckpoint = await _parentRunContinuationService
                        .TryResumeAfterChildCompletionAsync(
                            session,
                            childCheckpoint,
                            workspace.WorkspaceId,
                            runCancellationToken)
                        .ConfigureAwait(false);
                    return terminalParentCheckpoint ?? childCheckpoint;
                }

                return null;
            }

            Complete(
                approvedToolOutcome.Result?.IsError == true
                    ? AgentPendingPermissionStatus.Failed
                    : AgentPendingPermissionStatus.Executed,
                approvedToolOutcome.Result?.Summary ?? "Approved tool executed.");

            AgentProviderRunCapabilities runCapabilities;
            AgentModelVariantDescriptor? modelVariant;
            AgentModelSpeedOptionDescriptor? modelSpeedOption;
            AgentModelModeOptionDescriptor? modelModeOption;
            try
            {
                var metadata = await _providerResolver
                    .ResolveRunMetadataAsync(
                        providerSelection,
                        runCancellationToken
                    )
                    .ConfigureAwait(false);
                runCapabilities = metadata.RunCapabilities;
                modelVariant = metadata.ModelVariant;
                modelSpeedOption = metadata.ModelSpeedOption;
                modelModeOption = metadata.ModelModeOption;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failedCheckpoint = TransitionOrLatest(AgentRunStatus.Failed, ex.Message);
                var failedParentCheckpoint = await _parentRunContinuationService
                    .TryResumeAfterChildCompletionAsync(
                        session,
                        failedCheckpoint,
                        workspace.WorkspaceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return failedParentCheckpoint ?? failedCheckpoint;
            }

            if (!runCapabilities.SupportsNativeToolCalling)
            {
                var completedCheckpoint = TransitionOrLatest(
                    AgentRunStatus.Completed,
                    "Approved tool call executed.");
                var completedParentCheckpoint = await _parentRunContinuationService
                    .TryResumeAfterChildCompletionAsync(
                        session,
                        completedCheckpoint,
                        workspace.WorkspaceId,
                        runCancellationToken)
                    .ConfigureAwait(false);
                return completedParentCheckpoint ?? completedCheckpoint;
            }

            var runningCheckpoint = TransitionOrLatest(
                AgentRunStatus.Running,
                $"Approved tool '{pending.ToolId}' completed. Continuing provider execution.");
            var executionBinding = ResolveExecutionBinding(workspace);
            using var behaviorLoop = _behaviorLoopResolver.Resolve(profile);
            var loopResult = await behaviorLoop
                .RunAsync(
                    new AgentBehaviorLoopContext(
                        session,
                        profile,
                        providerSelection.Descriptor!.ProviderId,
                        chatBinding.ModelId,
                        runCapabilities,
                        workspace,
                        executionBinding,
                        pending.RunId,
                        pending.RunRevision,
                        runningCheckpoint,
                        runStartedAtUtc,
                        pending.UserMessage,
                        pending.UserTurnId,
                        modelVariant,
                        modelSpeedOption,
                        modelModeOption
                    ),
                    host,
                    runCancellationToken
                )
                .ConfigureAwait(false);
            if (loopResult.Checkpoint.Status == AgentRunStatus.Running
                && loopResult.CompletionKind == AgentBehaviorLoopCompletionKind.Interrupted)
            {
                loopResult = new AgentBehaviorLoopResult(
                    TransitionOrLatest(
                        AgentRunStatus.Interrupted,
                        "Approved permission resume was canceled after provider execution started."),
                    AgentBehaviorLoopCompletionKind.Interrupted);
            }
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Information,
                sessionId,
                pending.RunId,
                pending.RunRevision,
                "permission.approved_resume.completed",
                loopResult.CompletionKind.ToString(),
                resumeStopwatch.ElapsedMilliseconds
            );
            var parentCheckpoint = await _parentRunContinuationService
                .TryResumeAfterChildCompletionAsync(
                    session,
                    loopResult.Checkpoint,
                    workspace.WorkspaceId,
                    runCancellationToken
                )
                .ConfigureAwait(false);
            return parentCheckpoint ?? loopResult.Checkpoint;
        }
        catch (OperationCanceledException)
        {
            var cancellationSummary = providerSelection.IsRetiring
                ? $"Package '{providerSelection.OwnerPackageId}' became unavailable during approved permission resume."
                : "Approved permission resume was canceled.";
            Complete(AgentPendingPermissionStatus.Failed, cancellationSummary);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Warning,
                sessionId,
                pending.RunId,
                pending.RunRevision,
                "permission.approved_resume.canceled",
                cancellationSummary,
                resumeStopwatch.ElapsedMilliseconds
            );
            var canceledCheckpoint = _sessionService.GetLatestCheckpoint(
                sessionId,
                pending.RunRevision);
            if (canceledCheckpoint is null
                || canceledCheckpoint.RunRevision != pending.RunRevision
                || canceledCheckpoint.Status is not (AgentRunStatus.Stopped or AgentRunStatus.Interrupted))
            {
                canceledCheckpoint = _sessionService.TryTransitionRun(
                    runLease,
                    AgentRunStatus.Interrupted,
                    cancellationSummary)?.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(
                        sessionId,
                        pending.RunRevision);
            }

            if (canceledCheckpoint is null)
            {
                return null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return canceledCheckpoint;
            }

            var canceledParentCheckpoint = await _parentRunContinuationService
                .TryResumeAfterChildCompletionAsync(
                    session,
                    canceledCheckpoint,
                    workspace.WorkspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            return canceledParentCheckpoint ?? canceledCheckpoint;
        }
        catch (Exception ex)
        {
            Complete(AgentPendingPermissionStatus.Failed, ex.Message);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                sessionId,
                pending.RunId,
                pending.RunRevision,
                "permission.approved_resume.failed",
                ex.Message,
                resumeStopwatch.ElapsedMilliseconds,
                exception: ex);
            var failedCheckpoint = TransitionOrLatest(AgentRunStatus.Failed, ex.Message);
            var failedParentCheckpoint = await _parentRunContinuationService
                .TryResumeAfterChildCompletionAsync(
                    session,
                    failedCheckpoint,
                    workspace.WorkspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            return failedParentCheckpoint ?? failedCheckpoint;
        }
        finally
        {
            _activeRunRegistry.Complete(sessionId, pending.RunId, pending.RunRevision);
            runHandle.CancellationTokenSource.Dispose();
        }

        async ValueTask<bool> BeginApprovedExecutionAsync(CancellationToken cancellationToken)
        {
            using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                var currentProfile = ResolveProfile(pending.ProfileId);
                var currentWorkspace = ResolveWorkspace(pending.WorkspaceId);
                if (currentProfile is null || currentWorkspace is null)
                {
                    return false;
                }
                var currentBinding = ResolveExecutionBinding(currentWorkspace);
                if (cancellationToken.IsCancellationRequested
                    || !providerSelection.CanAcquireExactOwner()
                    || !AgentPermissionFingerprint.MatchesExecutionContext(
                        pending.ExecutionSnapshotJson,
                        pending,
                        currentProfile,
                        providerSelection.Descriptor?.ProviderId,
                        chatBinding.ModelId,
                        currentWorkspace,
                        currentBinding)
                    || !_activeRunRegistry.IsCurrent(
                        sessionId,
                        pending.RunId,
                        pending.RunRevision))
                {
                    return false;
                }

                return _permissionService.MarkExecutionStarted(
                    pending,
                    approveForSession);
            }
        }

        AgentRunCheckpointRecord TransitionOrLatest(AgentRunStatus status, string summary)
            => _sessionService.TryTransitionRun(runLease, status, summary)?.Checkpoint
               ?? _sessionService.GetLatestCheckpoint(sessionId, pending.RunRevision)
               ?? throw new InvalidOperationException("Permission continuation has no durable checkpoint.");
    }

    public Task<AgentRunCheckpointRecord?> DenyAsync(
        Guid sessionId,
        string requestId,
        CancellationToken cancellationToken = default)
        => _backgroundWork is null
            ? DenyCoreAsync(sessionId, requestId, cancellationToken)
            : _backgroundWork.RunOwnedAsync(
                ownedToken => DenyCoreAsync(sessionId, requestId, ownedToken),
                cancellationToken);

    private async Task<AgentRunCheckpointRecord?> DenyCoreAsync(
        Guid sessionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        AgentPendingPermissionDecisionResult decision;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            decision = _permissionService.TryDenyPendingRequest(
                sessionId,
                requestId,
                "Permission request denied.");
        }
        if (!decision.IsDecided || decision.Request is null)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        ReleasePreparedAuthority(decision.Request);

        var finalization = decision.Finalization
            ?? throw new InvalidOperationException("The denied permission suspension did not produce a finalization result.");
        var toolResultTurn = decision.ToolResultTurn
            ?? throw new InvalidOperationException("The denied permission suspension did not produce a tool result.");
        _sessionService.PublishCommittedPermissionDecision(finalization, toolResultTurn);
        var session = _sessionService.GetSession(sessionId);
        var stoppedCheckpoint = decision.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(sessionId)
            ?? throw new InvalidOperationException("The denied permission suspension did not produce a checkpoint.");
        if (session is not null)
        {
            await _parentRunContinuationService.TryResumeAfterChildCompletionAsync(
                session,
                stoppedCheckpoint,
                session.WorkspaceId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        return stoppedCheckpoint;
    }

    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

    private void PublishPermissionFinalization(AgentCheckpointPersistenceResult? finalization)
    {
        if (finalization is not null)
        {
            _sessionService.PublishCommittedCheckpoint(finalization);
        }
    }

    private AgentProfileRecord? ResolveProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? null : _profileService.GetProfile(profileId);

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord? workspace) =>
        workspace is null
            ? null
            : _workspaceService
                .ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(binding =>
                    binding.IsEnabled
                    && string.Equals(
                        binding.Role,
                        AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

    private void ReleasePreparedAuthority(AgentPendingPermissionRequestRecord request)
    {
        if (request.ToolExecutionId is { } executionId)
        {
            _toolService?.ReleasePreparedInvocation(executionId);
        }
    }
}

using System.Diagnostics;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentPermissionResumeCoordinator(
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
        CancellationToken cancellationToken,
        CancellationToken? admissionCancellationToken = null,
        Action? admitted = null)
    {
        AgentPendingPermissionClaimResult claim;
        AgentPendingPermissionDecisionResult? policyDenial;
        var claimToken = admissionCancellationToken ?? cancellationToken;
        var permissionActions = await _permissionService.ListActionsAsync(claimToken).ConfigureAwait(false);
        using (await _transitionGate.EnterAsync(sessionId, claimToken).ConfigureAwait(false))
        {
            claimToken.ThrowIfCancellationRequested();
            if (_sessionService.GetSession(sessionId) is { } claimSession
                && _deletionFence.IsFenced(claimSession))
            {
                admitted?.Invoke();
                return _sessionService.GetLatestCheckpoint(sessionId);
            }
            (claim, policyDenial) = _permissionService.TryClaimPendingRequestForApproval(
                sessionId,
                requestId,
                permissionActions,
                approveForSession);
        }
        if (policyDenial is not null)
        {
            admitted?.Invoke();
            return await CompleteDeniedDecisionAsync(policyDenial, cancellationToken)
                .ConfigureAwait(false);
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

            admitted?.Invoke();
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        try
        {
            // The claim and session-approval intent are durable. Acknowledge
            // admission before running the independently owned continuation.
            admitted?.Invoke();
            return await ApproveClaimedAsync(
                pending,
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

            await ReleasePreparedAuthorityAsync(pending).ConfigureAwait(false);

            return checkpoint
                ?? _sessionService.GetLatestCheckpoint(sessionId, pending.RunRevision)
                ?? _sessionService.GetLatestCheckpoint(sessionId);
        }
    }

    private async Task<AgentRunCheckpointRecord?> ApproveClaimedAsync(
        AgentPendingPermissionRequestRecord pending,
        CancellationToken cancellationToken)
    {
        var sessionId = pending.SessionId;

        async ValueTask CompleteAsync(AgentPendingPermissionStatus status, string summary)
        {
            _permissionService.CompleteClaimedRequest(pending, status, summary);
            await ReleasePreparedAuthorityAsync(pending).ConfigureAwait(false);
        }

        async ValueTask<AgentRunCheckpointRecord?> FinalizeSuspensionAsync(
            AgentPendingPermissionStatus status,
            AgentRunStatus runStatus,
            string summary)
        {
            var finalization = _permissionService.FinalizeClaimedRequest(
                pending,
                status,
                runStatus,
                summary);
            await ReleasePreparedAuthorityAsync(pending).ConfigureAwait(false);
            PublishPermissionFinalization(finalization);
            return finalization?.Checkpoint;
        }

        var session = _sessionService.GetSession(sessionId);
        if (session is null)
        {
            return await FinalizeSuspensionAsync(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The session used for this permission request was not found.").ConfigureAwait(false)
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        if (string.Equals(
                pending.BoundaryId,
                AgentPermissionBoundaryIds.OutsideConfiguredScope,
                StringComparison.OrdinalIgnoreCase)
            && (pending.ResourceClaimSetVersion != 1 || pending.ResourceClaims.Count == 0))
        {
            return await FinalizeSuspensionAsync(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Interrupted,
                       $"{AgentToolResultErrorCodes.PermissionReapprovalRequired}: Outside resource authority is not restorable; submit the tool call for explicit reapproval.").ConfigureAwait(false)
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        if (!string.Equals(session.WorkspaceId, pending.WorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            const string summary = "The session workspace changed after permission was requested.";
            return await FinalizeSuspensionAsync(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       summary).ConfigureAwait(false)
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        var workspace = ResolveWorkspace(pending.WorkspaceId);
        if (workspace is null)
        {
            return await FinalizeSuspensionAsync(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The workspace used for this run was not found.").ConfigureAwait(false)
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        var profile = ResolveProfile(pending.ProfileId);
        if (profile is null)
        {
            return await FinalizeSuspensionAsync(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The agent used for this run was not found.").ConfigureAwait(false)
                   ?? _sessionService.GetLatestCheckpoint(sessionId);
        }

        // The approved action does not need a model. Select one only when its
        // recorded result is ready to be returned to the provider loop.
        var providerSelection = new AgentRunProviderSelection(null);

        AgentActiveRunHandle? runHandle = null;
        AgentDurableRunLease? runLease = null;
        DateTimeOffset runStartedAtUtc = default;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (_deletionFence.IsFenced(session))
            {
                await CompleteAsync(AgentPendingPermissionStatus.Expired, "The session is being deleted.").ConfigureAwait(false);
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
                if (await FinalizeSuspensionAsync(
                        AgentPendingPermissionStatus.Expired,
                        AgentRunStatus.Interrupted,
                        summary).ConfigureAwait(false) is null)
                {
                    await CompleteAsync(AgentPendingPermissionStatus.Expired, summary).ConfigureAwait(false);
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
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                DurableLease = runLease,
            };
            var activation = _activeRunRegistry.Activate(sessionId, runHandle);
            if (!activation.IsAccepted)
            {
                await CompleteAsync(AgentPendingPermissionStatus.Expired, "A newer run superseded this permission request.").ConfigureAwait(false);
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
            var host = CreateHost();
            var approvedToolOutcome = await host.HandleApprovedToolCallAsync(
                    pending,
                    runCancellationToken,
                    BeginApprovedExecutionAsync
                )
                .ConfigureAwait(false);
            if (approvedToolOutcome.Kind == AgentToolCallOutcomeKind.WaitingForApproval)
            {
                await CompleteAsync(
                    AgentPendingPermissionStatus.Executed,
                    approvedToolOutcome.Result?.Summary
                    ?? "Approved tool execution is waiting for a child run.").ConfigureAwait(false);
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
                await CompleteAsync(
                    terminalStatus,
                    approvedToolOutcome.Result?.Summary ?? "Approved tool execution did not complete.").ConfigureAwait(false);
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

            await CompleteAsync(
                approvedToolOutcome.Result?.IsError == true
                    ? AgentPendingPermissionStatus.Failed
                    : AgentPendingPermissionStatus.Executed,
                approvedToolOutcome.Result?.Summary ?? "Approved tool executed.").ConfigureAwait(false);

            profile = ResolveProfile(pending.ProfileId) ?? profile;
            providerSelection = await _providerResolver.ResolveChatProviderAsync(profile, runCancellationToken).ConfigureAwait(false);
            var chatBinding = providerSelection.ChatBinding;
            if (!providerSelection.IsAvailable || string.IsNullOrWhiteSpace(chatBinding?.ModelId))
            {
                var interrupted = TransitionOrLatest(AgentRunStatus.Interrupted,
                    "Approved tool completed. Select an available chat model to continue.");
                return await _parentRunContinuationService.TryResumeAfterChildCompletionAsync(
                    session, interrupted, workspace.WorkspaceId, runCancellationToken).ConfigureAwait(false) ?? interrupted;
            }
            using var continuationLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                runCancellationToken, providerSelection.RetirementToken);
            runCancellationToken = continuationLifetime.Token;
            host = CreateHost();

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
            var behaviorLoop = await _behaviorLoopResolver.ResolveAsync(profile, runCancellationToken).ConfigureAwait(false);
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
            await CompleteAsync(AgentPendingPermissionStatus.Failed, cancellationSummary).ConfigureAwait(false);
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
            await CompleteAsync(AgentPendingPermissionStatus.Failed, ex.Message).ConfigureAwait(false);
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
            if (_sessionService.GetRun(pending.RunId)?.Status == AgentDurableRunStatus.WaitingForApproval)
            {
                _sessionService.PublishCommittedSessionChanged(sessionId);
            }
            runHandle.CancellationTokenSource.Dispose();
        }

        async ValueTask<bool> BeginApprovedExecutionAsync(AgentPermissionRequest? currentPermission, CancellationToken cancellationToken)
        {
            // Discover remote defaults before taking the local transition gate. The final
            // context check, current overrides and durable approval commit stay atomic.
            var currentPermissionActions = await _permissionService.ListActionsAsync(cancellationToken).ConfigureAwait(false);
            using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                var currentWorkspace = ResolveWorkspace(pending.WorkspaceId);
                if (currentWorkspace is null)
                {
                    return false;
                }
                var currentBinding = ResolveExecutionBinding(currentWorkspace);
                if (cancellationToken.IsCancellationRequested
                    || !AgentPermissionFingerprint.MatchesExecutionContext(
                        pending.ExecutionSnapshotJson,
                        pending,
                         currentWorkspace,
                        currentBinding)
                    || !_activeRunRegistry.IsCurrent(
                        sessionId,
                        pending.RunId,
                        pending.RunRevision))
                {
                    return false;
                }

                return _permissionService.CommitApprovedExecution(
                    pending,
                    currentPermissionActions,
                    pending.ApproveForSession,
                    currentPermission);
            }
        }

        AgentBehaviorLoopHost CreateHost() => _behaviorLoopHostFactory.Create(
            providerSelection, session, profile, workspace, pending.RunId, pending.RunRevision,
            runStartedAtUtc, pending.UserMessage, pending.UserTurnId, ResolveExecutionBinding(workspace));

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
        CancellationToken cancellationToken,
        CancellationToken? admissionCancellationToken = null,
        Action? admitted = null)
    {
        AgentPendingPermissionDecisionResult decision;
        var decisionToken = admissionCancellationToken ?? cancellationToken;
        using (await _transitionGate.EnterAsync(sessionId, decisionToken).ConfigureAwait(false))
        {
            decisionToken.ThrowIfCancellationRequested();
            decision = _permissionService.TryDenyPendingRequest(
                sessionId,
                requestId,
                "Permission request denied.");
        }
        admitted?.Invoke();
        if (!decision.IsDecided || decision.Request is null)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        return await CompleteDeniedDecisionAsync(decision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRunCheckpointRecord?> CompleteDeniedDecisionAsync(
        AgentPendingPermissionDecisionResult decision,
        CancellationToken cancellationToken)
    {
        if (!decision.IsDecided || decision.Request is null)
        {
            return decision.Request is null
                ? null
                : _sessionService.GetLatestCheckpoint(decision.Request.SessionId);
        }

        await ReleasePreparedAuthorityAsync(decision.Request).ConfigureAwait(false);
        var finalization = decision.Finalization
            ?? throw new InvalidOperationException("The denied permission suspension did not produce a finalization result.");
        var toolResultTurn = decision.ToolResultTurn
            ?? throw new InvalidOperationException("The denied permission suspension did not produce a tool result.");
        _sessionService.PublishCommittedPermissionDecision(finalization, toolResultTurn);
        var session = _sessionService.GetSession(decision.Request.SessionId);
        var stoppedCheckpoint = decision.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(decision.Request.SessionId)
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

    private async ValueTask ReleasePreparedAuthorityAsync(AgentPendingPermissionRequestRecord request)
    {
        if (request.ToolExecutionId is { } executionId && _toolService is not null)
        {
            await _toolService.ReleasePreparedInvocationAsync(executionId).ConfigureAwait(false);
        }
    }
}

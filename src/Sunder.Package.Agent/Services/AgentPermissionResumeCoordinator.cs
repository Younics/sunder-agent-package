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
    AgentSessionTransitionGate? transitionGate = null
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

    public async Task<AgentRunCheckpointRecord?> ApproveAsync(Guid sessionId, string requestId)
    {
        AgentPendingPermissionClaimResult claim;
        using (await _transitionGate.EnterAsync(sessionId).ConfigureAwait(false))
        {
            claim = _permissionService.TryClaimPendingRequest(sessionId, requestId);
        }
        if (!claim.IsClaimed || claim.Request is not { } pending)
        {
            if (claim.Outcome == AgentPendingPermissionClaimOutcome.InvalidSuspension)
            {
                _permissionService.ExpireActiveRequest(
                    sessionId,
                    requestId,
                    "The permission request no longer matches the current suspended run.");
            }

            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        try
        {
            return await ApproveClaimedAsync(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var runStatus = ex is OperationCanceledException
                ? AgentRunStatus.Interrupted
                : AgentRunStatus.Failed;
            var summary = ex is OperationCanceledException
                ? "Approved permission resume was canceled before execution ownership was established."
                : $"Approved permission resume failed before execution ownership was established: {ex.Message}";
            var checkpoint = _permissionService.FinalizeClaimedRequest(
                pending,
                AgentPendingPermissionStatus.Failed,
                runStatus,
                summary);
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

            return checkpoint
                ?? _sessionService.GetLatestCheckpoint(sessionId, pending.RunRevision)
                ?? _sessionService.GetLatestCheckpoint(sessionId);
        }
    }

    private async Task<AgentRunCheckpointRecord?> ApproveClaimedAsync(
        AgentPendingPermissionRequestRecord pending)
    {
        var sessionId = pending.SessionId;

        void Complete(AgentPendingPermissionStatus status, string summary)
            => _permissionService.CompleteClaimedRequest(pending, status, summary);

        AgentRunCheckpointRecord? FinalizeSuspension(
            AgentPendingPermissionStatus status,
            AgentRunStatus runStatus,
            string summary)
            => _permissionService.FinalizeClaimedRequest(
                pending,
                status,
                runStatus,
                summary);

        var session = _sessionService.GetSession(sessionId);
        if (session is null)
        {
            return FinalizeSuspension(
                       AgentPendingPermissionStatus.Expired,
                       AgentRunStatus.Failed,
                       "The session used for this permission request was not found.")
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

        var providerSelection = _providerResolver.ResolveChatProvider(profile);
        var chatBinding = providerSelection.ChatBinding;
        var provider = providerSelection.Provider;
        if (
            provider is null
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
        using (await _transitionGate.EnterAsync(sessionId).ConfigureAwait(false))
        {
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
            runLease = new AgentDurableRunLease(resumedRun);
            runHandle = new AgentActiveRunHandle(
                pending.RunId,
                pending.RunRevision,
                pending.CreatedAtUtc,
                profile.ProfileId,
                pending.UserMessage,
                new CancellationTokenSource())
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
                provider,
                session,
                profile,
                workspace,
                pending.RunId,
                pending.RunRevision,
                pending.CreatedAtUtc,
                pending.UserMessage,
                pending.UserTurnId
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
                        provider,
                        chatBinding,
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
                        CancellationToken.None)
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
            var behaviorLoop = _behaviorLoopResolver.Resolve(profile);
            var loopResult = await behaviorLoop
                .RunAsync(
                    new AgentBehaviorLoopContext(
                        session,
                        profile,
                        provider.Descriptor.ProviderId,
                        chatBinding.ModelId,
                        runCapabilities,
                        workspace,
                        executionBinding,
                        pending.RunId,
                        pending.RunRevision,
                        runningCheckpoint,
                        pending.CreatedAtUtc,
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
            Complete(AgentPendingPermissionStatus.Failed, "Approved permission resume was canceled.");
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Warning,
                sessionId,
                pending.RunId,
                pending.RunRevision,
                "permission.approved_resume.canceled",
                "Approved permission resume was canceled.",
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
                    "Approved permission resume was canceled.")?.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(
                        sessionId,
                        pending.RunRevision);
            }

            if (canceledCheckpoint is null)
            {
                return null;
            }

            var canceledParentCheckpoint = await _parentRunContinuationService
                .TryResumeAfterChildCompletionAsync(
                    session,
                    canceledCheckpoint,
                    workspace.WorkspaceId,
                    CancellationToken.None)
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
                    CancellationToken.None)
                .ConfigureAwait(false);
            return failedParentCheckpoint ?? failedCheckpoint;
        }
        finally
        {
            _activeRunRegistry.CleanupCurrent(sessionId, pending.RunId, pending.RunRevision);
            runHandle.CancellationTokenSource.Dispose();
        }

        async ValueTask<bool> BeginApprovedExecutionAsync(CancellationToken cancellationToken)
        {
            using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                return !cancellationToken.IsCancellationRequested
                       && _activeRunRegistry.IsCurrent(
                           sessionId,
                           pending.RunId,
                           pending.RunRevision)
                       && _permissionService.MarkExecutionStarted(pending);
            }
        }

        AgentRunCheckpointRecord TransitionOrLatest(AgentRunStatus status, string summary)
            => _sessionService.TryTransitionRun(runLease, status, summary)?.Checkpoint
               ?? _sessionService.GetLatestCheckpoint(sessionId, pending.RunRevision)
               ?? throw new InvalidOperationException("Permission continuation has no durable checkpoint.");
    }

    public async Task<AgentRunCheckpointRecord?> DenyAsync(Guid sessionId, string requestId)
    {
        AgentPendingPermissionDecisionResult decision;
        using (await _transitionGate.EnterAsync(sessionId).ConfigureAwait(false))
        {
            decision = _permissionService.TryDenyPendingRequest(
                sessionId,
                requestId,
                "Permission request denied.");
        }
        if (!decision.IsDecided || decision.Request is not { } pending)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        var session = _sessionService.GetSession(sessionId);
        _sessionService.AppendToolResultTurn(
            sessionId,
            pending.CallId,
            pending.ToolId ?? string.Empty,
            pending.ArgumentsJson,
            $"Permission denied: tool '{pending.ToolId}' was not executed.",
            "Permission request denied.",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: true,
            errorCode: "permission-denied",
            backendId: null
        );
        var stoppedCheckpoint = decision.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(sessionId)
            ?? throw new InvalidOperationException("The denied permission suspension did not produce a checkpoint.");
        if (session is not null)
        {
            await _parentRunContinuationService.TryResumeAfterChildCompletionAsync(
                session,
                stoppedCheckpoint,
                session.WorkspaceId ?? string.Empty,
                CancellationToken.None).ConfigureAwait(false);
        }

        return stoppedCheckpoint;
    }

    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

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
}

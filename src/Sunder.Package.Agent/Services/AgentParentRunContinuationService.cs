using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;

namespace Sunder.Package.Agent.Services;

public sealed class AgentParentRunContinuationService(
    AgentSessionService sessionService,
    AgentProfileService profileService,
    AgentWorkspaceService workspaceService,
    AgentRunProviderResolver providerResolver,
    AgentActiveRunRegistry activeRunRegistry,
    AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
    AgentBehaviorLoopResolver behaviorLoopResolver,
    AgentChildRunSessionService childRunSessionService,
    AgentSessionTransitionGate? transitionGate = null,
    AgentSessionDeletionFence? deletionFence = null,
    AgentBackgroundWorkService? backgroundWork = null)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentBehaviorLoopHostFactory _behaviorLoopHostFactory = behaviorLoopHostFactory;
    private readonly AgentBehaviorLoopResolver _behaviorLoopResolver = behaviorLoopResolver;
    private readonly AgentChildRunSessionService _childRunSessionService = childRunSessionService;
    private readonly AgentSessionTransitionGate _transitionGate =
        transitionGate ?? AgentSessionTransitionGate.Shared;
    private readonly AgentSessionDeletionFence _deletionFence =
        deletionFence ?? AgentSessionDeletionFence.Shared;
    private readonly AgentBackgroundWorkService? _backgroundWork = backgroundWork;
    private int _recoveryStarted;

    public async Task<AgentRunCheckpointRecord?> TryResumeAfterChildCompletionAsync(
        AgentSessionRecord childSession,
        AgentRunCheckpointRecord childCheckpoint,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (childCheckpoint.SessionId != childSession.SessionId
            || !IsTerminal(childCheckpoint.Status)
            || childSession.ParentSessionId is not { } parentSessionId
            || childSession.ParentRunId is not { } parentRunId
            || childSession.ParentRunRevision is not { } parentRunRevision
            || string.IsNullOrWhiteSpace(childSession.ParentToolCallId))
        {
            return null;
        }

        AgentParentContinuationDispatchResult? dispatch = null;
        AgentDurableRunLease? lease = null;
        using (await _transitionGate.EnterAsync(parentSessionId, cancellationToken).ConfigureAwait(false))
        {
            var key = new AgentDurableRunKey(parentRunId, parentSessionId, parentRunRevision);
            var parentRun = _sessionService.GetRun(parentRunId);
            if (parentRun?.Key != key
                || parentRun.Status != AgentDurableRunStatus.WaitingForApproval
                || parentRun.Suspension is not AgentChildJoinRunSuspension childJoin
                || string.IsNullOrWhiteSpace(parentRun.ContinuationToken)
                || !childJoin.OutstandingTasks.Any(task =>
                    task.ChildSessionId == childSession.SessionId
                    && string.Equals(
                        task.ToolCallId,
                        childSession.ParentToolCallId,
                        StringComparison.Ordinal)))
            {
                return _sessionService.GetLatestCheckpoint(parentSessionId);
            }

            lease = new AgentDurableRunLease(parentRun);
            var transition = _sessionService.CompleteChildJoinTask(
                lease,
                parentRun.ContinuationToken,
                BuildChildTaskResult(childSession, childCheckpoint));
            switch (transition.Outcome)
            {
                case AgentChildJoinTransitionOutcome.Rejected:
                    if (_sessionService.GetLatestRun(parentSessionId) is { } latestRun
                        && latestRun.Key.RunRevision > parentRunRevision)
                    {
                        return _sessionService.TryTransitionRun(
                            lease,
                            AgentRunStatus.Interrupted,
                            "A newer run superseded the parent while its child completion was being recorded.")?.Checkpoint
                            ?? _sessionService.GetLatestCheckpoint(
                                parentSessionId,
                                parentRunRevision);
                    }

                    return _sessionService.GetLatestCheckpoint(parentSessionId);
                case AgentChildJoinTransitionOutcome.Waiting:
                    return _sessionService.GetLatestCheckpoint(parentSessionId);
                case AgentChildJoinTransitionOutcome.Ready:
                    if (transition.Work is null)
                    {
                        throw new InvalidOperationException(
                            "A completed child join did not persist parent continuation work.");
                    }

                    dispatch = _sessionService.TryClaimParentContinuationWork(
                        transition.Work.WorkId,
                        lease,
                        parentRun.ContinuationToken);
                    if (dispatch is null)
                    {
                        return InterruptStaleDispatch(
                            transition.Work.WorkId,
                            lease,
                            "A newer run superseded the parent continuation before it could be claimed.");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(transition.Outcome),
                        transition.Outcome,
                        "Unknown child-join transition outcome.");
            }
        }

        return dispatch is null || lease is null
            ? _sessionService.GetLatestCheckpoint(parentSessionId)
            : await DispatchAsync(dispatch, lease, cancellationToken).ConfigureAwait(false);
    }

    internal void StartRecovery()
    {
        if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0)
        {
            return;
        }

        var queued = _backgroundWork?.TryQueue(async cancellationToken =>
        {
            try
            {
                await ProcessPendingWorkAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Work remains durable and will be retried by the next explicit drain or package start.
            }
        }) == true;
        if (!queued)
        {
            Interlocked.Exchange(ref _recoveryStarted, 0);
        }
    }

    internal async Task ProcessPendingWorkAsync(CancellationToken cancellationToken)
    {
        _sessionService.Store.EnsureRuntimeGenerationCurrent();
        foreach (var work in _sessionService.ListDispatchableParentContinuationWork())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentParentContinuationDispatchResult? dispatch;
            AgentDurableRunLease lease;
            using (await _transitionGate
                       .EnterAsync(work.ParentRunKey.SessionId, cancellationToken)
                       .ConfigureAwait(false))
            {
                var run = _sessionService.GetRun(work.ParentRunKey.RunId);
                if (run?.Key != work.ParentRunKey || run.FinishedAtUtc is not null)
                {
                    _sessionService.CompleteParentContinuationWork(
                        work.WorkId,
                        failed: true,
                        "Parent run is no longer resumable.");
                    continue;
                }

                lease = new AgentDurableRunLease(run);
                dispatch = _sessionService.TryClaimParentContinuationWork(
                    work.WorkId,
                    lease,
                    run.ContinuationToken ?? string.Empty);
                if (dispatch is null)
                {
                    InterruptStaleDispatch(
                        work.WorkId,
                        lease,
                        "The parent continuation claim became stale before dispatch.");
                }
            }

            if (dispatch is not null)
            {
                await DispatchAsync(dispatch, lease, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentRunCheckpointRecord?> DispatchAsync(
        AgentParentContinuationDispatchResult dispatch,
        AgentDurableRunLease lease,
        CancellationToken cancellationToken)
    {
        var key = dispatch.Run.Key;
        var parentSession = _sessionService.GetSession(key.SessionId);
        var parentProfile = ResolveProfile(dispatch.Run.ProfileId);
        var parentUserTurn = _sessionService.GetTurn(dispatch.Join.UserTurnId);
        var workspace = ResolveWorkspace(parentSession?.WorkspaceId);
        if (parentSession is null
            || parentProfile is null
            || parentUserTurn is null
            || parentUserTurn.SessionId != key.SessionId
            || parentUserTurn.Role != AgentMessageRole.User
            || parentUserTurn.Kind != AgentTurnKind.Message
            || workspace is null)
        {
            return FailDispatch(
                dispatch.Work.WorkId,
                lease,
                "The durable parent continuation context is no longer available.");
        }

        using var providerSelection = _providerResolver.ResolveChatProvider(parentProfile);
        var chatBinding = providerSelection.ChatBinding;
        if (!providerSelection.IsAvailable
            || chatBinding is null
            || string.IsNullOrWhiteSpace(chatBinding.ModelId))
        {
            return FailDispatch(
                dispatch.Work.WorkId,
                lease,
                "No installed provider matches the durable parent profile, or no model is selected.");
        }

        AgentRunProviderMetadata metadata;
        try
        {
            metadata = await _providerResolver
                .ResolveRunMetadataAsync(providerSelection, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return InterruptStaleDispatch(
                dispatch.Work.WorkId,
                lease,
                "Parent continuation was canceled before provider execution started.");
        }
        catch (Exception ex)
        {
            return FailDispatch(dispatch.Work.WorkId, lease, ex.Message);
        }

        var runHandle = new AgentActiveRunHandle(
            key.RunId,
            key.RunRevision,
            dispatch.Run.StartedAtUtc,
            dispatch.Run.ProfileId,
            dispatch.Run.UserMessage,
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                providerSelection.RetirementToken))
        {
            DurableLease = lease,
        };
        using (await _transitionGate.EnterAsync(key.SessionId).ConfigureAwait(false))
        {
            if (_deletionFence.IsFenced(parentSession))
            {
                runHandle.CancellationTokenSource.Dispose();
                return InterruptStaleDispatch(
                    dispatch.Work.WorkId,
                    lease,
                    "Parent continuation was canceled because its session is being deleted.");
            }
            var activation = _activeRunRegistry.Activate(key.SessionId, runHandle);
            if (!activation.IsAccepted)
            {
                runHandle.CancellationTokenSource.Dispose();
                if (activation.CurrentRun.RunId == key.RunId
                    && activation.CurrentRun.RunRevision == key.RunRevision)
                {
                    ScheduleRetryAfterActiveUnwind(key, dispatch.Work.WorkId);
                }
                else
                {
                    InterruptStaleDispatch(
                        dispatch.Work.WorkId,
                        lease,
                        "A newer run superseded the parent continuation.");
                }

                return _sessionService.GetLatestCheckpoint(key.SessionId);
            }

            activation.DisplacedRun?.CancellationTokenSource.Cancel();
        }

        try
        {
            var runCancellationToken = runHandle.CancellationTokenSource.Token;
            using (await _transitionGate
                       .EnterAsync(key.SessionId, runCancellationToken)
                       .ConfigureAwait(false))
            {
                if (!_activeRunRegistry.IsCurrent(key.SessionId, key.RunId, key.RunRevision)
                    || !_sessionService.MarkParentContinuationExecutionStarted(
                        dispatch.Work.WorkId))
                {
                    throw new OperationCanceledException(runCancellationToken);
                }
            }

            AppendChildJoinResult(lease, dispatch.Join);

            if (!metadata.RunCapabilities.SupportsNativeToolCalling)
            {
                var completed = _sessionService.TryTransitionRun(
                    lease,
                    AgentRunStatus.Completed,
                    "Subagent result recorded.")?.Checkpoint;
                _sessionService.CompleteParentContinuationWork(
                    dispatch.Work.WorkId,
                    failed: completed is null,
                    completed is null ? "Parent completion transition was rejected." : null);
                return completed ?? _sessionService.GetLatestCheckpoint(key.SessionId);
            }

            var host = _behaviorLoopHostFactory.Create(
                providerSelection.Provider,
                parentSession,
                parentProfile,
                workspace,
                key.RunId,
                key.RunRevision,
                dispatch.Run.StartedAtUtc,
                dispatch.Run.UserMessage,
                parentUserTurn.TurnId,
                ResolveExecutionBinding(workspace));
            using var behaviorLoop = _behaviorLoopResolver.Resolve(parentProfile);
            var loopResult = await behaviorLoop.RunAsync(
                new AgentBehaviorLoopContext(
                    parentSession,
                    parentProfile,
                    providerSelection.Descriptor!.ProviderId,
                    chatBinding.ModelId,
                    metadata.RunCapabilities,
                    workspace,
                    ResolveExecutionBinding(workspace),
                    key.RunId,
                    key.RunRevision,
                    dispatch.RunningCheckpoint,
                    dispatch.Run.StartedAtUtc,
                    dispatch.Run.UserMessage,
                    parentUserTurn.TurnId,
                    metadata.ModelVariant,
                    metadata.ModelSpeedOption,
                    metadata.ModelModeOption),
                host,
                runCancellationToken).ConfigureAwait(false);
            if (loopResult.Checkpoint.Status == AgentRunStatus.Running
                && loopResult.CompletionKind == AgentBehaviorLoopCompletionKind.Interrupted)
            {
                var interrupted = _sessionService.TryTransitionRun(
                    lease,
                    AgentRunStatus.Interrupted,
                    "Parent continuation was canceled after provider execution started.")?.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(key.SessionId, key.RunRevision);
                if (interrupted is not null)
                {
                    loopResult = new AgentBehaviorLoopResult(
                        interrupted,
                        AgentBehaviorLoopCompletionKind.Interrupted);
                }
            }
            _sessionService.CompleteParentContinuationWork(
                dispatch.Work.WorkId,
                failed: loopResult.CompletionKind == AgentBehaviorLoopCompletionKind.Interrupted,
                error: loopResult.CompletionKind == AgentBehaviorLoopCompletionKind.Interrupted
                    ? "Parent continuation was canceled after provider execution started."
                    : null);
            var ancestor = await TryResumeAfterChildCompletionAsync(
                parentSession,
                loopResult.Checkpoint,
                workspace.WorkspaceId,
                runCancellationToken).ConfigureAwait(false);
            return ancestor ?? loopResult.Checkpoint;
        }
        catch (OperationCanceledException)
        {
            var cancellationSummary = providerSelection.IsRetiring
                ? $"Package '{providerSelection.OwnerPackageId}' became unavailable during parent continuation."
                : "Parent continuation was stopped or canceled.";
            var checkpoint = _sessionService.GetLatestCheckpoint(
                key.SessionId,
                key.RunRevision);
            if (checkpoint is null
                || checkpoint.RunRevision != key.RunRevision
                || checkpoint.Status is not (AgentRunStatus.Stopped or AgentRunStatus.Interrupted))
            {
                checkpoint = _sessionService.TryTransitionRun(
                    lease,
                    AgentRunStatus.Interrupted,
                    cancellationSummary)?.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(key.SessionId, key.RunRevision);
            }
            _sessionService.CompleteParentContinuationWork(
                dispatch.Work.WorkId,
                failed: true,
                cancellationSummary);
            return checkpoint;
        }
        catch (Exception ex)
        {
            return FailDispatch(dispatch.Work.WorkId, lease, ex.Message);
        }
        finally
        {
            _activeRunRegistry.Complete(key.SessionId, key.RunId, key.RunRevision);
            if (_sessionService.GetRun(key.RunId)?.Status == AgentDurableRunStatus.WaitingForApproval)
            {
                _sessionService.PublishCommittedSessionChanged(key.SessionId);
            }
            runHandle.CancellationTokenSource.Dispose();
        }
    }

    private AgentRunCheckpointRecord? FailDispatch(
        string workId,
        AgentDurableRunLease lease,
        string summary)
    {
        var checkpoint = _sessionService.TryTransitionRun(
            lease,
            AgentRunStatus.Failed,
            summary)?.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(
                lease.Key.SessionId,
                lease.Key.RunRevision);
        _sessionService.CompleteParentContinuationWork(workId, failed: true, summary);
        return checkpoint;
    }

    private AgentRunCheckpointRecord? InterruptStaleDispatch(
        string workId,
        AgentDurableRunLease lease,
        string summary)
    {
        var checkpoint = _sessionService.TryTransitionRun(
            lease,
            AgentRunStatus.Interrupted,
            summary)?.Checkpoint
            ?? _sessionService.GetLatestCheckpoint(
                lease.Key.SessionId,
                lease.Key.RunRevision);
        _sessionService.CompleteParentContinuationWork(workId, failed: true, summary);
        return checkpoint;
    }

    private void ScheduleRetryAfterActiveUnwind(AgentDurableRunKey key, string workId)
    {
        var queued = _backgroundWork?.TryQueue(async cancellationToken =>
        {
            try
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                    if (!_activeRunRegistry.IsCurrent(
                            key.SessionId,
                            key.RunId,
                            key.RunRevision))
                    {
                        await ProcessPendingWorkAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch
            {
                // Durable work remains dispatchable for startup recovery.
            }
        }) == true;
        if (!queued)
        {
            _sessionService.RecordParentContinuationRetryPending(
                workId,
                "Parent continuation retry remains durable because the background queue rejected dispatch.");
        }
    }

    private AgentChildJoinTaskResult BuildChildTaskResult(
        AgentSessionRecord childSession,
        AgentRunCheckpointRecord childCheckpoint)
    {
        var content = childCheckpoint.Status == AgentRunStatus.Completed
            ? _childRunSessionService.RenderLastAssistantText(childSession.SessionId)
              ?? childCheckpoint.Summary
              ?? "Subagent completed without visible output."
            : $"Subagent '{childSession.Title}' {childCheckpoint.Status.ToString().ToLowerInvariant()}: "
              + (childCheckpoint.Summary ?? "No additional details were recorded.");
        return new AgentChildJoinTaskResult(
            childSession.SessionId,
            childSession.ParentToolCallId!,
            childCheckpoint.Status,
            childCheckpoint.Summary ?? childCheckpoint.Status.ToString(),
            content,
            childSession.Title);
    }

    private void AppendChildJoinResult(
        AgentDurableRunLease lease,
        AgentChildJoinRunSuspension suspension)
    {
        var parentSessionId = lease.Key.SessionId;
        var toolCallId = suspension.CompletedTasks
            .Select(task => task.ToolCallId)
            .Distinct(StringComparer.Ordinal)
            .Single();
        var hasFailure = suspension.CompletedTasks.Any(task => task.Status != AgentRunStatus.Completed);
        var result = new AgentToolResult(
            suspension.ToolId,
            hasFailure
                ? "One or more subagent tasks ended without completing."
                : "Subagent tasks completed.",
            Content: string.Join("\n\n", suspension.CompletedTasks.Select(RenderTaskResult)),
            IsError: hasFailure,
            ErrorCode: hasFailure ? AgentToolResultErrorCodes.SubagentRunFailed : null);
        if (_sessionService.TryReplaceChildSuspensionToolResult(lease, toolCallId, result) is not null)
        {
            return;
        }

        var existingResult = _sessionService.ListTurns(parentSessionId)
            .SelectMany(turn => turn.Items)
            .FirstOrDefault(item => item.Kind == AgentTurnItemKind.ToolResult
                                    && string.Equals(item.CallId, toolCallId, StringComparison.Ordinal));
        if (existingResult is not null)
        {
            return;
        }

        _sessionService.AppendToolResultTurn(
            lease,
            toolCallId,
            suspension.ToolId,
            suspension.ArgumentsJson,
            result.Content,
            result.Summary,
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: result.IsError,
            errorCode: result.ErrorCode,
            backendId: null);
    }

    private static string RenderTaskResult(AgentChildJoinTaskResult task)
        => $"<task_result task_id=\"{task.ChildSessionId:N}\" status=\"{task.Status.ToString().ToLowerInvariant()}\">\n"
           + (task.Content ?? task.Summary)
           + "\n</task_result>";

    private static bool IsTerminal(AgentRunStatus status)
        => status is AgentRunStatus.Completed
            or AgentRunStatus.Failed
            or AgentRunStatus.Stopped
            or AgentRunStatus.Interrupted;

    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

    private AgentProfileRecord? ResolveProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : _profileService.GetProfile(profileId);

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord workspace)
        => _workspaceService.ListBindings(workspace.WorkspaceId)
            .FirstOrDefault(binding => binding.IsEnabled
                                       && string.Equals(
                                           binding.Role,
                                           AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                                           StringComparison.OrdinalIgnoreCase));
}
